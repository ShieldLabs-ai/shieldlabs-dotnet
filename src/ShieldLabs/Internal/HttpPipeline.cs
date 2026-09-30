using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShieldLabs.Internal;

internal enum ApiSurface
{
    History,
    Management,
}

/// <summary>
/// Sends authenticated GET requests with a per-attempt timeout and the SDK retry policy:
/// connection errors, timeouts and 5xx are retried with exponential backoff and jitter
/// (base 0.5 s, factor 2, cap 8 s); 429 is retried only when the caller allows it, after at least
/// 1 s when it has no <c>Retry-After</c>; a <c>Retry-After</c> header is followed as sent, capped at
/// 10 s; 400, 401, 402, 403 and 404 are never retried.
/// </summary>
internal sealed class HttpPipeline
{
    internal static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(0.5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shortest wait before retrying a 429 that came without <c>Retry-After</c>, and after any 429
    /// while <see cref="IdentificationsClient.GetAsync"/> waits: the History API limit is counted per
    /// second, so a sooner request would land in the same window.
    /// </summary>
    internal static readonly TimeSpan RateLimitedWait = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ApiSurface _surface;
    private readonly string _bearer;
    private readonly KeyValuePair<string, string>[] _extraHeaders;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly ITimeSource _time;

    internal HttpPipeline(
        HttpClient http,
        ApiSurface surface,
        string bearer,
        KeyValuePair<string, string>[] extraHeaders,
        TimeSpan timeout,
        int maxRetries,
        ITimeSource time)
    {
        _http = http;
        _surface = surface;
        _bearer = bearer;
        _extraHeaders = extraHeaders;
        _timeout = timeout;
        _maxRetries = maxRetries;
        _time = time;
    }

    internal ITimeSource Time => _time;

    internal string SurfaceName => _surface == ApiSurface.History ? "History API" : "Management API";

    /// <summary>Sends a GET request and returns the body of a 2xx response.</summary>
    /// <param name="uri">The request URI.</param>
    /// <param name="retryRateLimited">Whether a 429 is retried.</param>
    /// <param name="cancellationToken">Cancels the request and any backoff wait.</param>
    /// <param name="maxRetries">Overrides the client's retry count when given.</param>
    /// <param name="attemptTimeout">Caps the client's per-attempt timeout when given.</param>
    internal async Task<byte[]> GetAsync(
        Uri uri,
        bool retryRateLimited,
        CancellationToken cancellationToken,
        int? maxRetries = null,
        TimeSpan? attemptTimeout = null)
    {
        var retries = maxRetries ?? _maxRetries;
        var timeout = EffectiveTimeout(_timeout, attemptTimeout);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await SendOnceAsync(uri, timeout, cancellationToken).ConfigureAwait(false);
            if (outcome.Failure is null)
            {
                return outcome.Body!;
            }

            if (attempt >= retries || !IsRetryable(outcome.Failure, retryRateLimited))
            {
                throw outcome.Failure;
            }

            var rateLimited = outcome.Failure is RateLimitException;
            await _time.Delay(RetryDelay(attempt + 1, outcome.RetryAfter, rateLimited), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The per-attempt timeout: the client timeout, lowered to <paramref name="cap"/> when that is shorter.</summary>
    internal static TimeSpan EffectiveTimeout(TimeSpan clientTimeout, TimeSpan? cap)
    {
        if (cap is not TimeSpan limit)
        {
            return clientTimeout;
        }

        if (clientTimeout == Timeout.InfiniteTimeSpan)
        {
            return limit;
        }

        return limit < clientTimeout ? limit : clientTimeout;
    }

    internal static bool IsRetryable(ShieldLabsException failure, bool retryRateLimited) => failure switch
    {
        RateLimitException => retryRateLimited,
        ServerException => true,
        ApiConnectionException => true,
        ApiTimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// Delay before retry number <paramref name="retryNumber"/> (1-based): <c>Retry-After</c> as sent,
    /// capped at 10 s, when present (<c>Retry-After: 0</c> or a date in the past retries at once),
    /// otherwise min(8 s, 0.5 s × 2^(n-1)) scaled by a random factor in [0.5, 1), and at least
    /// <see cref="RateLimitedWait"/> when the failure was a 429 (<paramref name="rateLimited"/>).
    /// </summary>
    internal TimeSpan RetryDelay(int retryNumber, TimeSpan? retryAfter, bool rateLimited)
    {
        if (retryAfter is TimeSpan requested)
        {
            return requested < MaxRetryAfter ? requested : MaxRetryAfter;
        }

        var backoff = Math.Min(MaxBackoff.TotalSeconds, BaseDelay.TotalSeconds * Math.Pow(2, retryNumber - 1));
        var jitter = 0.5 + (0.5 * _time.NextJitter());
        var delay = TimeSpan.FromSeconds(backoff * jitter);
        return rateLimited && delay < RateLimitedWait ? RateLimitedWait : delay;
    }

    private async Task<Outcome> SendOnceAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _time.CancelAfter(attemptCts, timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);
            foreach (var header in _extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCts.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, attemptCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status >= 200 && status <= 299)
            {
                return new Outcome(body, null, null);
            }

            var retryAfter = ParseRetryAfter(response, _time);
            return new Outcome(null, CreateApiException(_surface, status, body, CopyHeaders(response), retryAfter), retryAfter);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new Outcome(null, new ApiTimeoutException($"The ShieldLabs {SurfaceName} request did not complete within {DescribeTimeout(timeout)}.", ex), null);
        }
        catch (HttpRequestException ex)
        {
            return new Outcome(null, new ApiConnectionException($"Could not reach the ShieldLabs {SurfaceName}: {ex.Message}", ex), null);
        }
        catch (IOException ex)
        {
            return new Outcome(null, new ApiConnectionException($"Could not reach the ShieldLabs {SurfaceName}: {ex.Message}", ex), null);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return Array.Empty<byte>();
        }

#if NET8_0_OR_GREATER
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
    }

    /// <summary>Maps an HTTP error status to the SDK exception type. Never throws.</summary>
    internal static ApiException CreateApiException(
        ApiSurface surface,
        int status,
        byte[] body,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        TimeSpan? retryAfter)
    {
        var text = DecodeBody(body);
        var error = ParseErrorText(body);
        var message = BuildMessage(surface, status, error);
        switch (status)
        {
            case 400:
                return new BadRequestException(message, status, text, error, headers);
            case 401:
            case 403:
                return new AuthenticationException(message, status, text, error, headers);
            case 402:
                return new QuotaExceededException(message, status, text, error, headers);
            case 404:
                return new NotFoundException(message, status, text, error, headers);
            case 429:
                return new RateLimitException(message, status, text, error, headers, retryAfter);
            default:
                return status >= 500 && status <= 599
                    ? new ServerException(message, status, text, error, headers)
                    : new ApiException(message, status, text, error, headers);
        }
    }

    internal static string DecodeBody(byte[] body)
    {
        try
        {
            return Encoding.UTF8.GetString(body);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>The <c>error</c> field of a JSON object body, or a bare JSON string body; otherwise null.</summary>
    internal static string? ParseErrorText(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            var root = JsonUtil.Parse(body);
            if (root.ValueKind == JsonValueKind.String)
            {
                return JsonUtil.ReadString(root);
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return JsonUtil.ReadString(error);
            }
        }
        catch (JsonException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private static string BuildMessage(ApiSurface surface, int status, string? error)
    {
        var surfaceName = surface == ApiSurface.History ? "History API" : "Management API";
        var builder = new StringBuilder();
        builder.Append("ShieldLabs ").Append(surfaceName).Append(" request failed with HTTP ")
            .Append(status.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(error))
        {
            var shortError = error!.Length > 300 ? error.Substring(0, 300) + "..." : error;
            builder.Append(": ").Append(shortError);
        }

        builder.Append('.');
        var hint = Hint(surface, status);
        if (hint.Length > 0)
        {
            builder.Append(' ').Append(hint);
        }

        return builder.ToString();
    }

    private static string Hint(ApiSurface surface, int status)
    {
        switch (status)
        {
            case 401:
            case 403:
                return surface == ApiSurface.History
                    ? "Check that ApiKey is the Private API Key (sec_...) of this domain."
                    : "Check the Secret Key and that Domain is the registered domain.";
            case 402:
                return "The account has no identifications left in its included volume.";
            case 404:
                return "Check the base URL.";
            case 429:
                return surface == ApiSurface.History
                    ? "The History API allows about 15 requests per second per domain."
                    : "The Management API allows about 15 requests per minute per IP and then blocks the IP for 10 minutes: cache the profile.";
            default:
                return string.Empty;
        }
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CopyHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = new List<string>(header.Value);
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = new List<string>(header.Value);
            }
        }

        return headers;
    }

    /// <summary>Reads <c>Retry-After</c> as seconds (integer or decimal) or as an HTTP date.</summary>
    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response, ITimeSource time)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            var text = value.Trim();
            if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                && !double.IsNaN(seconds)
                && !double.IsInfinity(seconds))
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 86400));
            }

            if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                var delta = date - time.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
        }

        return null;
    }

    private static string DescribeTimeout(TimeSpan timeout)
        => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private readonly struct Outcome
    {
        internal Outcome(byte[]? body, ShieldLabsException? failure, TimeSpan? retryAfter)
        {
            Body = body;
            Failure = failure;
            RetryAfter = retryAfter;
        }

        internal byte[]? Body { get; }

        internal ShieldLabsException? Failure { get; }

        internal TimeSpan? RetryAfter { get; }
    }
}
