using System;
using System.Collections.Generic;
using System.Net.Http;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Options of <see cref="ShieldLabsClient"/>.</summary>
public sealed class ShieldLabsClientOptions
{
    /// <summary>Default History API origin.</summary>
    public const string DefaultBaseUrl = "https://account.shieldlabs.ai";

    /// <summary>
    /// Private API Key of the domain (<c>sec_…</c>), sent as <c>Authorization: Bearer</c>. Required.
    /// Keep it on your server; it is never logged by the SDK.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// History API origin. Defaults to <see cref="DefaultBaseUrl"/>. Request paths start with
    /// <c>/api/v1/</c>, so a trailing <c>/api</c> in this value is removed. Must use https; plain
    /// http is accepted only for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Timeout of one HTTP attempt. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times a failed request is retried (connection errors, timeouts, 429 and 5xx).
    /// Defaults to 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
}

/// <summary>
/// Client of the ShieldLabs History API: read the verdict of an identification by its request ID
/// and search the history of a device, user, visitor, session, cookie or IP.
/// </summary>
/// <remarks>
/// Create one instance per domain and reuse it: it is safe for concurrent use. When no
/// <see cref="HttpClient"/> is passed, a shared process-wide instance is used.
/// </remarks>
public sealed class ShieldLabsClient
{
    /// <summary>Creates a History API client.</summary>
    /// <param name="options">Client options; <see cref="ShieldLabsClientOptions.ApiKey"/> is required.</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> to send requests with (for example one from
    /// <c>IHttpClientFactory</c>). The SDK never disposes it.
    /// </param>
    /// <exception cref="ValidationException">An option is missing or invalid.</exception>
    public ShieldLabsClient(ShieldLabsClientOptions options, HttpClient? httpClient = null)
        : this(options, httpClient, SystemTimeSource.Instance)
    {
    }

    internal ShieldLabsClient(ShieldLabsClientOptions options, HttpClient? httpClient, ITimeSource time)
    {
        if (options is null)
        {
            throw new ValidationException("options is required.");
        }

        var apiKey = Validation.Credential(options.ApiKey, "ApiKey");
        Validation.WarnIfUnexpectedApiKey(apiKey);
        BaseUrl = Validation.BaseUrl(options.BaseUrl, ShieldLabsClientOptions.DefaultBaseUrl, stripApiSuffix: true, "BaseUrl");
        Validation.Timeout(options.Timeout, "Timeout");
        Validation.MaxRetries(options.MaxRetries);

        var pipeline = new HttpPipeline(
            httpClient ?? DefaultHttp.Instance,
            ApiSurface.History,
            apiKey,
            Array.Empty<KeyValuePair<string, string>>(),
            options.Timeout,
            options.MaxRetries,
            time);
        History = new HistoryClient(pipeline, BaseUrl);
        Identifications = new IdentificationsClient(History, time);
    }

    /// <summary>Read one identification by request ID, optionally waiting for the verdict.</summary>
    public IdentificationsClient Identifications { get; }

    /// <summary>Search identifications by device, user, visitor, request, session, cookie or IP.</summary>
    public HistoryClient History { get; }

    /// <summary>The normalized History API origin requests are sent to.</summary>
    public string BaseUrl { get; }
}
