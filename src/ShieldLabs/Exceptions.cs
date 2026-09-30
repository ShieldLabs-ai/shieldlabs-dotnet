using System;
using System.Collections.Generic;

namespace ShieldLabs;

/// <summary>Base class of every exception thrown by the ShieldLabs SDK.</summary>
public class ShieldLabsException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ShieldLabsException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the underlying cause.</summary>
    public ShieldLabsException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The server answered with an HTTP error status. Specific statuses use the subclasses
/// <see cref="BadRequestException"/>, <see cref="AuthenticationException"/>,
/// <see cref="QuotaExceededException"/>, <see cref="NotFoundException"/>,
/// <see cref="RateLimitException"/> and <see cref="ServerException"/>.
/// </summary>
public class ApiException : ShieldLabsException
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoHeaders =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="body">Raw response body text (empty when the server sent none).</param>
    /// <param name="error">Error text parsed from the body, when there was one.</param>
    /// <param name="headers">Response headers (case-insensitive names).</param>
    public ApiException(
        string message,
        int statusCode,
        string? body = null,
        string? error = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body ?? string.Empty;
        Error = error;
        Headers = headers ?? NoHeaders;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Raw response body text. Empty when the server sent no body.</summary>
    public string Body { get; }

    /// <summary>
    /// Error text parsed from the body: the <c>error</c> field of a JSON object, or a bare JSON
    /// string. Null when the body carried no such text (for example an empty body or HTML).
    /// </summary>
    public string? Error { get; }

    /// <summary>Response headers, keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
}

/// <summary>HTTP 400: the request was rejected as invalid.</summary>
public sealed class BadRequestException : ApiException
{
    /// <inheritdoc cref="ApiException(string, int, string?, string?, IReadOnlyDictionary{string, IReadOnlyList{string}}?)"/>
    public BadRequestException(string message, int statusCode = 400, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message, statusCode, body, error, headers)
    {
    }
}

/// <summary>HTTP 401 or 403: the key (or, for the Management API, the domain and Secret Key pair) was not accepted. Never retried.</summary>
public sealed class AuthenticationException : ApiException
{
    /// <inheritdoc cref="ApiException(string, int, string?, string?, IReadOnlyDictionary{string, IReadOnlyList{string}}?)"/>
    public AuthenticationException(string message, int statusCode = 401, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message, statusCode, body, error, headers)
    {
    }
}

/// <summary>HTTP 402: the account has no identifications left in its included volume.</summary>
public sealed class QuotaExceededException : ApiException
{
    /// <inheritdoc cref="ApiException(string, int, string?, string?, IReadOnlyDictionary{string, IReadOnlyList{string}}?)"/>
    public QuotaExceededException(string message, int statusCode = 402, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message, statusCode, body, error, headers)
    {
    }
}

/// <summary>HTTP 404: the path does not exist (for example a wrong base URL).</summary>
public sealed class NotFoundException : ApiException
{
    /// <inheritdoc cref="ApiException(string, int, string?, string?, IReadOnlyDictionary{string, IReadOnlyList{string}}?)"/>
    public NotFoundException(string message, int statusCode = 404, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message, statusCode, body, error, headers)
    {
    }
}

/// <summary>HTTP 429: too many requests.</summary>
public sealed class RateLimitException : ApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="body">Raw response body text.</param>
    /// <param name="error">Error text parsed from the body.</param>
    /// <param name="headers">Response headers.</param>
    /// <param name="retryAfter">Value of the <c>Retry-After</c> header, when present.</param>
    public RateLimitException(string message, int statusCode = 429, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null, TimeSpan? retryAfter = null)
        : base(message, statusCode, body, error, headers)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// How long the server asked the client to wait (<c>Retry-After</c>), or null when it did not
    /// say. <c>Retry-After: 0</c> and a date in the past give zero.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>HTTP 5xx: the server or a proxy in front of it failed.</summary>
public sealed class ServerException : ApiException
{
    /// <inheritdoc cref="ApiException(string, int, string?, string?, IReadOnlyDictionary{string, IReadOnlyList{string}}?)"/>
    public ServerException(string message, int statusCode = 500, string? body = null, string? error = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
        : base(message, statusCode, body, error, headers)
    {
    }
}

/// <summary>The request could not reach the server (DNS, TCP or TLS failure, connection reset).</summary>
public sealed class ApiConnectionException : ShieldLabsException
{
    /// <summary>Creates the exception.</summary>
    public ApiConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>An HTTP attempt did not complete within the configured timeout.</summary>
public sealed class ApiTimeoutException : ShieldLabsException
{
    /// <summary>Creates the exception.</summary>
    public ApiTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A webhook delivery did not carry a valid <c>X-Shield-Signature</c> for any of the given secrets.</summary>
public sealed class SignatureVerificationException : ShieldLabsException
{
    /// <summary>Creates the exception.</summary>
    public SignatureVerificationException(string message)
        : base(message)
    {
    }
}

/// <summary>A verified webhook body could not be parsed into an event.</summary>
public sealed class WebhookParseException : ShieldLabsException
{
    /// <summary>Creates the exception.</summary>
    public WebhookParseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>An argument or option is invalid. Thrown before any HTTP request is sent.</summary>
public sealed class ValidationException : ShieldLabsException
{
    /// <summary>Creates the exception.</summary>
    public ValidationException(string message)
        : base(message)
    {
    }
}
