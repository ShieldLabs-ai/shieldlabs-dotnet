using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace ShieldLabs.Internal;

/// <summary>Client-side checks that run before any HTTP call.</summary>
internal static class Validation
{
    internal const int MinLimit = 1;
    internal const int MaxLimit = 100;

    /// <summary>
    /// Characters a User HID path segment keeps unescaped besides ASCII letters and digits. The
    /// History API decodes the segment only when it is written in exactly this canonical form
    /// (every other byte of the UTF-8 text as uppercase <c>%XX</c>); any other spelling, such as
    /// <c>%40</c> for <c>@</c>, is compared literally and matches nothing.
    /// </summary>
    private const string UserHidUnescaped = "-._~$&+,:;=@";

    private const string UpperHexDigits = "0123456789ABCDEF";

    private static readonly char[] PathDelimiters = { '/', '?', '#' };

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>The History API path segment for a lookup type.</summary>
    internal static string WireName(LookupType type) => type switch
    {
        LookupType.Ip => WireSearchHistoryValues.SearchTypeIp,
        LookupType.UserHid => WireSearchHistoryValues.SearchTypeUserHid,
        LookupType.VisitorId => WireSearchHistoryValues.SearchTypeVisitorId,
        LookupType.RequestId => WireSearchHistoryValues.SearchTypeRequestId,
        LookupType.DeviceId => WireSearchHistoryValues.SearchTypeDeviceId,
        LookupType.SessionId => WireSearchHistoryValues.SearchTypeSessionId,
        LookupType.CookieId => WireSearchHistoryValues.SearchTypeCookieId,
        _ => throw new ValidationException(
            $"Unknown lookup type {(int)type}. Use one of: ip, user_hid, visitor_id, request_id, device_id, session_id, cookie_id."),
    };

    /// <summary>
    /// Validates a History lookup value for <paramref name="type"/> and returns the path segment to
    /// send: UUIDs lowercased, IPv4 addresses unchanged, User HIDs percent-encoded in the canonical
    /// form the History API matches (see <see cref="UserHidSegment"/>).
    /// </summary>
    internal static string LookupSegment(LookupType type, string? value, string parameterName)
    {
        var wire = WireName(type);
        if (value is null)
        {
            throw new ValidationException($"{parameterName} is required for a {wire} lookup.");
        }

        switch (type)
        {
            case LookupType.UserHid:
                return UserHidSegment(value, parameterName);
            case LookupType.Ip:
                if (!IsIPv4(value))
                {
                    throw new ValidationException(
                        $"{parameterName} must be a dotted IPv4 address such as 203.0.113.24. IPv6 addresses are not searchable.");
                }

                return value;
            default:
                if (!IsUuid(value))
                {
                    throw new ValidationException(
                        $"{parameterName} must be a UUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx) for a {wire} lookup.");
                }

                return value.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Encodes a User HID as one path segment the History API can match: ASCII letters, digits and
    /// <c>- . _ ~ $ &amp; + , : ; = @</c> stay as they are, every other byte of the UTF-8 text becomes
    /// uppercase <c>%XX</c>. Values that cannot be matched are rejected: a value that contains
    /// <c>/</c> (the server compares <c>%2F</c> literally and routes a raw <c>/</c> elsewhere), the
    /// values <c>.</c> and <c>..</c> (URL handling removes them as dot segments) and text with an
    /// unpaired surrogate (it has no UTF-8 form).
    /// </summary>
    internal static string UserHidSegment(string value, string parameterName)
    {
        if (value.Length == 0)
        {
            throw new ValidationException($"{parameterName} must be a non-empty User HID.");
        }

        if (value == "." || value == "..")
        {
            throw new ValidationException(
                $"{parameterName} \"{value}\" cannot be searched: URL handling removes \".\" and \"..\" path segments, so the request would read a different path.");
        }

        if (value.IndexOf('/') >= 0)
        {
            throw new ValidationException(
                $"{parameterName} cannot be searched because it contains \"/\": the History API cannot match User HIDs with a slash. Use User HIDs without \"/\", such as the hex output of UserHid.FromUserId.");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ValidationException($"{parameterName} must be valid Unicode text (it contains an unpaired surrogate).");
        }

        var segment = new StringBuilder(bytes.Length + 8);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || UserHidUnescaped.IndexOf(c) >= 0)
            {
                segment.Append(c);
            }
            else
            {
                segment.Append('%').Append(UpperHexDigits[b >> 4]).Append(UpperHexDigits[b & 0xF]);
            }
        }

        return segment.ToString();
    }

    /// <summary>8-4-4-4-12 hexadecimal digits, any version, nil UUID allowed.</summary>
    internal static bool IsUuid(string value)
    {
        if (value.Length != 36)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-')
                {
                    return false;
                }
            }
            else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Four decimal octets 0-255 separated by dots, without leading zeros.</summary>
    internal static bool IsIPv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            if (int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return true;
    }

    internal static void Limit(int limit, string name)
    {
        if (limit < MinLimit || limit > MaxLimit)
        {
            throw new ValidationException($"{name} must be between {MinLimit} and {MaxLimit} (got {limit}).");
        }
    }

    internal static void Offset(int offset)
    {
        if (offset < 0)
        {
            throw new ValidationException($"Offset must be zero or greater (got {offset}).");
        }
    }

    /// <summary>
    /// Trims a credential and rejects empty values and characters that cannot be sent in an HTTP
    /// header (only visible ASCII is allowed). Never echoes the value.
    /// </summary>
    internal static string Credential(string? value, string name)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ValidationException($"{name} is required.");
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                throw new ValidationException($"{name} contains control characters.");
            }

            if (!IsVisibleAscii(c))
            {
                throw new ValidationException(
                    $"{name} contains characters that cannot be sent in an HTTP header (only visible ASCII characters are allowed).");
            }
        }

        return trimmed;
    }

    /// <summary>Printable ASCII without the space: U+0021 to U+007E.</summary>
    private static bool IsVisibleAscii(char c) => c >= '!' && c <= '~';

    /// <summary>Warns (never fails) when a Private API Key does not look like sec_xxxxxxxx-xxxxxxxx-xxxxxxxx.</summary>
    internal static bool LooksLikePrivateApiKey(string key)
    {
        if (key.Length != 30 || !key.StartsWith("sec_", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 4; i < key.Length; i++)
        {
            var c = key[i];
            if (i == 12 || i == 21)
            {
                if (c != '-')
                {
                    return false;
                }
            }
            else if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    internal static void WarnIfUnexpectedApiKey(string key)
    {
        if (!LooksLikePrivateApiKey(key))
        {
            Trace.TraceWarning(
                "ShieldLabs: the ApiKey does not look like a Private API Key (sec_xxxxxxxx-xxxxxxxx-xxxxxxxx). " +
                "The History API accepts only the Private API Key of the domain.");
        }
    }

    /// <summary>
    /// Normalizes a base URL: absolute https URL without query or fragment, no trailing slash.
    /// Plain http is accepted only for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>,
    /// <c>::1</c>), because every request carries a key. When <paramref name="stripApiSuffix"/> is
    /// set, a trailing <c>/api</c> is removed as well so request paths never become <c>/api/api/...</c>.
    /// </summary>
    internal static string BaseUrl(string? value, string fallback, bool stripApiSuffix, string name)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new ValidationException($"{name} must be an absolute https URL without query string or fragment.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri))
        {
            throw new ValidationException(
                $"{name} must use https: every request carries a key. Plain http is accepted only for loopback hosts (localhost, 127.0.0.1, ::1) in development and tests.");
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (stripApiSuffix && normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - 4).TrimEnd('/');
        }

        return normalized;
    }

    /// <summary><c>localhost</c> (any case) or a loopback IP address (127.0.0.0/8, ::1).</summary>
    internal static bool IsLoopbackHost(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
            && IPAddress.TryParse(uri.DnsSafeHost, out var address)
            && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Normalizes a registered domain the way the server stores it: trimmed, lowercase, without
    /// scheme, credentials, path, query, fragment, trailing slash or a leading <c>www.</c>.
    /// </summary>
    internal static string Domain(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            text = text.Substring(schemeEnd + 3);
        }
        else if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = text.Substring(2);
        }

        var pathStart = text.IndexOfAny(PathDelimiters);
        if (pathStart >= 0)
        {
            text = text.Substring(0, pathStart);
        }

        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            text = text.Substring(at + 1);
        }

        if (text.StartsWith("www.", StringComparison.Ordinal))
        {
            text = text.Substring(4);
        }

        if (text.Length == 0)
        {
            throw new ValidationException("Domain is required: the registered domain, for example example.com.");
        }

        foreach (var c in text)
        {
            if (c > '~')
            {
                throw new ValidationException("Domain must be ASCII: pass the punycode form (xn--...) of an internationalized domain.");
            }
        }

        foreach (var c in text)
        {
            if (!IsVisibleAscii(c))
            {
                throw new ValidationException("Domain must be a host name such as example.com.");
            }
        }

        return text;
    }

    internal static void Timeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ValidationException($"{name} must be greater than zero.");
        }
    }

    internal static void MaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
        {
            throw new ValidationException("MaxRetries must be zero or greater.");
        }
    }
}
