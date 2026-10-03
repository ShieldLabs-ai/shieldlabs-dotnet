using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Options of <see cref="ManagementClient"/>.</summary>
public sealed class ManagementClientOptions
{
    /// <summary>Default Management API origin.</summary>
    public const string DefaultBaseUrl = "https://api.shieldlabs.ai";

    /// <summary>Secret Key of the domain, sent as <c>Authorization: Bearer</c>. Required.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The registered domain, sent as <c>X-Shield-Domain</c>. Required. It is normalized before
    /// sending (trimmed, lowercased, without scheme, path, trailing slash and a leading <c>www.</c>),
    /// because the server matches the registered domain exactly.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Management API origin. Defaults to <see cref="DefaultBaseUrl"/>. Must use https; plain http
    /// is accepted only for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Timeout of one HTTP attempt. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times a failed request is retried (connection errors, timeouts and 5xx; never 429).
    /// Defaults to 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
}

/// <summary>
/// Client of the ShieldLabs Management API. It reads the domain profile, including the remaining
/// included identifications of the account.
/// </summary>
/// <remarks>
/// The Management API allows about 15 requests per minute per caller IP and then blocks that IP
/// for 10 minutes, so this client never retries a 429. Call it sparingly and cache the profile.
/// The client is safe for concurrent use.
/// </remarks>
public sealed class ManagementClient
{
    private readonly HttpPipeline _pipeline;
    private readonly string _baseUrl;

    /// <summary>Creates a Management API client.</summary>
    /// <param name="options">Client options; <see cref="ManagementClientOptions.SecretKey"/> and <see cref="ManagementClientOptions.Domain"/> are required.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. The SDK never disposes it.</param>
    /// <exception cref="ValidationException">An option is missing or invalid.</exception>
    public ManagementClient(ManagementClientOptions options, HttpClient? httpClient = null)
        : this(options, httpClient, SystemTimeSource.Instance)
    {
    }

    internal ManagementClient(ManagementClientOptions options, HttpClient? httpClient, ITimeSource time)
    {
        if (options is null)
        {
            throw new ValidationException("options is required.");
        }

        var secretKey = Validation.Credential(options.SecretKey, "SecretKey");
        Domain = Validation.Domain(options.Domain);
        _baseUrl = Validation.BaseUrl(options.BaseUrl, ManagementClientOptions.DefaultBaseUrl, stripApiSuffix: false, "BaseUrl");
        Validation.Timeout(options.Timeout, "Timeout");
        Validation.MaxRetries(options.MaxRetries);

        _pipeline = new HttpPipeline(
            httpClient ?? DefaultHttp.Instance,
            ApiSurface.Management,
            secretKey,
            new[] { Wire.Parameter<string>(WireGetDomainProfileParameters.XShieldDomain, Domain) },
            options.Timeout,
            options.MaxRetries,
            time);
    }

    /// <summary>The normalized domain sent as <c>X-Shield-Domain</c>.</summary>
    public string Domain { get; }

    /// <summary>Reads the domain profile (<c>GET /v1/profile</c>).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="AuthenticationException">The domain and Secret Key pair was rejected.</exception>
    /// <exception cref="RateLimitException">The caller IP is rate limited; not retried.</exception>
    /// <exception cref="ApiException">Another HTTP error status.</exception>
    public async Task<DomainProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var body = await _pipeline.GetAsync(new Uri(_baseUrl + "/v1/profile", UriKind.Absolute), retryRateLimited: false, cancellationToken).ConfigureAwait(false);
        return ParseProfile(body);
    }

    internal static DomainProfile ParseProfile(byte[] body)
    {
        JsonElement root;
        try
        {
            root = JsonUtil.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ApiException("The ShieldLabs Management API returned a body that is not valid JSON: " + ex.Message, 200, HttpPipeline.DecodeBody(body));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException("The ShieldLabs Management API returned an unexpected body (expected a JSON object).", 200, HttpPipeline.DecodeBody(body));
        }

        try
        {
            return new DomainProfile
            {
                Domain = JsonUtil.AsString(Wire.Read<string>(root, WireGetDomainProfileResponse.Domain)),
                RemainingIdentifications = JsonUtil.AsLong(Wire.Read<long>(root, WireGetDomainProfileResponse.Weight)),
                PublicKeyMasked = JsonUtil.AsString(Wire.Read<string>(root, WireGetDomainProfileResponse.PublicKey)),
                SecretKeyMasked = JsonUtil.AsString(Wire.Read<string>(root, WireGetDomainProfileResponse.Secret)),
                CreatedAt = Timestamps.ParseRfc3339(JsonUtil.StringOrNull(Wire.Read<string>(root, WireGetDomainProfileResponse.CreatedAt))),
                Raw = root,
            };
        }
        catch (Exception ex) when (ex is not ShieldLabsException && ex is not OutOfMemoryException)
        {
            // Defensive: a profile that cannot be read is reported as an API error, never as an unrelated exception.
            throw new ApiException("The ShieldLabs Management API returned a profile that could not be read: " + ex.Message, 200, HttpPipeline.DecodeBody(body));
        }
    }
}

/// <summary>The Management API profile of one registered domain.</summary>
public sealed class DomainProfile
{
    /// <summary>The registered domain.</summary>
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// Remaining included identifications of the account. Can be negative when the account is over
    /// its included volume.
    /// </summary>
    [JsonPropertyName("remaining_identifications")]
    public long RemainingIdentifications { get; init; }

    /// <summary>Public Key with every character except the last 4 replaced by <c>*</c>.</summary>
    [JsonPropertyName("public_key_masked")]
    public string PublicKeyMasked { get; init; } = string.Empty;

    /// <summary>Secret Key with every character except the last 4 replaced by <c>*</c>.</summary>
    [JsonPropertyName("secret_key_masked")]
    public string SecretKeyMasked { get; init; } = string.Empty;

    /// <summary>
    /// When the domain was created (UTC), or null when the server sent no parsable timestamp.
    /// Serialized as <c>2026-01-15T09:00:00.000Z</c>.
    /// </summary>
    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(NullableUtcTimestampConverter))]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The original response object, including fields this model does not map.</summary>
    [JsonPropertyName("raw")]
    public JsonElement Raw { get; init; } = JsonUtil.EmptyObject;
}
