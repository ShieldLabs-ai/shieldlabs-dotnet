using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>The identifier a History API search matches on.</summary>
public enum LookupType
{
    /// <summary>Public IPv4 address (<c>ip</c>). IPv6 addresses are not searchable.</summary>
    Ip,

    /// <summary>
    /// Your User HID, matched exactly and case-sensitively (<c>user_hid</c>). Values that contain
    /// <c>/</c>, and the values <c>.</c> and <c>..</c>, cannot be searched.
    /// </summary>
    UserHid,

    /// <summary>Visitor ID, a UUID (<c>visitor_id</c>).</summary>
    VisitorId,

    /// <summary>Request ID of one identification, a UUID (<c>request_id</c>).</summary>
    RequestId,

    /// <summary>Device ID, a UUID (<c>device_id</c>).</summary>
    DeviceId,

    /// <summary>Session ID, a UUID (<c>session_id</c>).</summary>
    SessionId,

    /// <summary>Cookie ID, a UUID (<c>cookie_id</c>).</summary>
    CookieId,
}

/// <summary>Paging options of <see cref="HistoryClient.SearchAsync"/>.</summary>
public sealed class HistorySearchOptions
{
    /// <summary>Rows per page, 1 to 100. Defaults to 20.</summary>
    public int Limit { get; set; } = 20;

    /// <summary>Rows to skip, zero or greater. Defaults to 0.</summary>
    public int Offset { get; set; }
}

/// <summary>Options of <see cref="HistoryClient.IterateAsync"/>.</summary>
public sealed class HistoryIterateOptions
{
    /// <summary>Rows requested per page, 1 to 100. Defaults to 100.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Stop after this many identifications. Null (the default) reads until the end.</summary>
    public int? MaxItems { get; set; }
}

/// <summary>One page of History API results, newest first.</summary>
public sealed class HistoryPage
{
    /// <summary>Identifications on this page, newest first.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<Identification> Data { get; init; } = Array.Empty<Identification>();

    /// <summary>Total number of identifications that match the search.</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }
}

/// <summary>
/// History API searches (<c>GET /api/v1/history/{type}/{value}</c>). Obtain it from
/// <see cref="ShieldLabsClient.History"/>.
/// </summary>
/// <remarks>
/// Arguments are validated before any request is sent, because the server does not reject bad
/// input: an unknown type returns unfiltered rows and a malformed UUID or IP returns a 500.
/// </remarks>
public sealed class HistoryClient
{
    private readonly HttpPipeline _pipeline;
    private readonly string _baseUrl;

    internal HistoryClient(HttpPipeline pipeline, string baseUrl)
    {
        _pipeline = pipeline;
        _baseUrl = baseUrl;
    }

    /// <summary>Reads one page of identifications that match an identifier, newest first.</summary>
    /// <param name="type">Which identifier to match.</param>
    /// <param name="value">
    /// The identifier: a UUID for the ID types (sent lowercase), a dotted IPv4 address for
    /// <see cref="LookupType.Ip"/>, or a non-empty User HID (matched exactly as given; values that
    /// contain <c>/</c>, and the values <c>.</c> and <c>..</c>, throw <see cref="ValidationException"/>).
    /// </param>
    /// <param name="options">Paging options (limit 1 to 100, default 20; offset, default 0).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ValidationException">An argument is invalid; nothing was sent.</exception>
    /// <exception cref="ApiException">The server answered with an error status.</exception>
    /// <exception cref="ApiConnectionException">The server could not be reached.</exception>
    /// <exception cref="ApiTimeoutException">The request timed out.</exception>
    public Task<HistoryPage> SearchAsync(
        LookupType type,
        string value,
        HistorySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var wireType = Validation.WireName(type);
        var segment = Validation.LookupSegment(type, value, nameof(value));
        var limit = options?.Limit ?? 20;
        var offset = options?.Offset ?? 0;
        Validation.Limit(limit, "Limit");
        Validation.Offset(offset);
        return SearchCoreAsync(wireType, segment, limit, offset, retryRateLimited: true, cancellationToken);
    }

    /// <summary>
    /// Iterates over every identification that matches an identifier, newest first, reading pages
    /// lazily. Rows are deduplicated on the request ID, because offset paging can repeat a row when
    /// new identifications arrive while iterating. Iteration stops at the reported total, at an
    /// empty page or after <see cref="HistoryIterateOptions.MaxItems"/> items.
    /// </summary>
    /// <param name="type">Which identifier to match.</param>
    /// <param name="value">The identifier (see <see cref="SearchAsync"/>).</param>
    /// <param name="options">Page size (1 to 100, default 100) and an optional item cap.</param>
    /// <param name="cancellationToken">Cancels the iteration.</param>
    /// <exception cref="ValidationException">An argument is invalid; nothing was sent.</exception>
    public IAsyncEnumerable<Identification> IterateAsync(
        LookupType type,
        string value,
        HistoryIterateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var wireType = Validation.WireName(type);
        var segment = Validation.LookupSegment(type, value, nameof(value));
        var pageSize = options?.PageSize ?? 100;
        var maxItems = options?.MaxItems;
        Validation.Limit(pageSize, "PageSize");
        if (maxItems < 0)
        {
            throw new ValidationException("MaxItems must be zero or greater.");
        }

        return IterateCoreAsync(wireType, segment, pageSize, maxItems, cancellationToken);
    }

    /// <summary>
    /// Reads one page. <paramref name="segment"/> is the validated, already encoded path segment
    /// (see <see cref="Validation.LookupSegment"/>). <paramref name="maxRetries"/> and
    /// <paramref name="attemptTimeout"/> override the client settings when given.
    /// </summary>
    internal async Task<HistoryPage> SearchCoreAsync(
        string wireType,
        string segment,
        int limit,
        int offset,
        bool retryRateLimited,
        CancellationToken cancellationToken,
        int? maxRetries = null,
        TimeSpan? attemptTimeout = null)
    {
        var limitParameter = Wire.Parameter<long>(WireSearchHistoryParameters.Limit, limit);
        var offsetParameter = Wire.Parameter<long>(WireSearchHistoryParameters.Offset, offset);
        var typeParameter = Wire.Parameter<string>(WireSearchHistoryParameters.SearchType, wireType);
        var valueParameter = Wire.Parameter<string>(WireSearchHistoryParameters.Value, segment);
        var url = _baseUrl
            + "/api/v1/history/" + typeParameter.Value + "/" + valueParameter.Value
            + "?" + limitParameter.Key + "=" + limitParameter.Value
            + "&" + offsetParameter.Key + "=" + offsetParameter.Value;
        var body = await _pipeline.GetAsync(new Uri(url, UriKind.Absolute), retryRateLimited, cancellationToken, maxRetries, attemptTimeout).ConfigureAwait(false);
        return ParsePage(body);
    }

    internal static HistoryPage ParsePage(byte[] body)
    {
        JsonElement root;
        try
        {
            root = JsonUtil.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ApiException("The ShieldLabs History API returned a body that is not valid JSON: " + ex.Message, 200, HttpPipeline.DecodeBody(body));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException("The ShieldLabs History API returned an unexpected body (expected a JSON object).", 200, HttpPipeline.DecodeBody(body));
        }

        var items = new List<Identification>();
        if (Wire.Read<WireArray<WireHistoryRow>>(root, WireSearchHistoryResponse.Data) is JsonElement data && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                try
                {
                    items.Add(Normalizer.FromHistoryRow(row));
                }
                catch (Exception ex) when (ex is not ShieldLabsException && ex is not OutOfMemoryException)
                {
                    // Defensive: a row that cannot be read is reported as an API error, never as an unrelated exception.
                    throw new ApiException("The ShieldLabs History API returned a row that could not be read: " + ex.Message, 200, HttpPipeline.DecodeBody(body));
                }
            }
        }

        var totalElement = Wire.Read<long>(root, WireSearchHistoryResponse.Total);
        var total = totalElement is JsonElement t && t.ValueKind == JsonValueKind.Number
            ? JsonUtil.AsLong(t, items.Count)
            : items.Count;
        return new HistoryPage { Data = items, Total = total };
    }

    private async IAsyncEnumerable<Identification> IterateCoreAsync(
        string wireType,
        string segment,
        int pageSize,
        int? maxItems,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (maxItems == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var yielded = 0;
        while (true)
        {
            var page = await SearchCoreAsync(wireType, segment, pageSize, offset, retryRateLimited: true, cancellationToken).ConfigureAwait(false);
            if (page.Data.Count == 0)
            {
                yield break;
            }

            foreach (var identification in page.Data)
            {
                if (!seen.Add(identification.RequestId))
                {
                    continue;
                }

                yield return identification;
                yielded++;
                if (maxItems is int cap && yielded >= cap)
                {
                    yield break;
                }
            }

            offset += page.Data.Count;
            if (offset >= page.Total)
            {
                yield break;
            }
        }
    }
}
