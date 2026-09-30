using System;
using System.Security.Cryptography;
using System.Text;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>
/// Verifies the <c>X-Shield-Signature</c> header of a webhook delivery:
/// <c>sha256=</c> followed by the hex HMAC-SHA256 of the raw request body, keyed with the endpoint
/// signing secret (the full string, including its <c>whsec_</c> prefix).
/// </summary>
/// <remarks>
/// Always verify the raw body bytes as received; never re-serialize parsed JSON. Pass several
/// secrets to rotate a signing secret without downtime: the delivery is valid when any matches.
/// </remarks>
public static class WebhookSignature
{
    /// <summary>Name of the signature header.</summary>
    public const string HeaderName = "X-Shield-Signature";

    private const string Prefix = "sha256=";
    private const int DigestHexLength = 64;

    /// <summary>
    /// Returns true when <paramref name="signatureHeader"/> is a valid signature of
    /// <paramref name="payload"/> for at least one of <paramref name="secrets"/>. Never throws:
    /// a missing or malformed header, an empty signature or no usable secret returns false.
    /// </summary>
    /// <param name="payload">The raw request body, decoded as UTF-8.</param>
    /// <param name="signatureHeader">The <c>X-Shield-Signature</c> header value.</param>
    /// <param name="secrets">One or more endpoint signing secrets (<c>whsec_…</c>). Empty entries are ignored.</param>
    public static bool Verify(string payload, string? signatureHeader, params string[] secrets)
        => Check(payload is null ? null : Encoding.UTF8.GetBytes(payload), signatureHeader, secrets) == SignatureCheck.Valid;

    /// <summary>
    /// Returns true when <paramref name="signatureHeader"/> is a valid signature of
    /// <paramref name="payload"/> for at least one of <paramref name="secrets"/>. Never throws:
    /// a missing or malformed header, an empty signature or no usable secret returns false.
    /// </summary>
    /// <param name="payload">The raw request body bytes.</param>
    /// <param name="signatureHeader">The <c>X-Shield-Signature</c> header value.</param>
    /// <param name="secrets">One or more endpoint signing secrets (<c>whsec_…</c>). Empty entries are ignored.</param>
    public static bool Verify(byte[] payload, string? signatureHeader, params string[] secrets)
        => Check(payload, signatureHeader, secrets) == SignatureCheck.Valid;

    internal static SignatureCheck Check(byte[]? payload, string? signatureHeader, string[]? secrets)
    {
        if (payload is null)
        {
            return SignatureCheck.MissingPayload;
        }

        var header = signatureHeader?.Trim() ?? string.Empty;
        if (header.Length == 0)
        {
            return SignatureCheck.MissingHeader;
        }

        if (!header.StartsWith(Prefix, StringComparison.Ordinal)
            || header.Length != Prefix.Length + DigestHexLength
            || !Hex.TryDecode(header.Substring(Prefix.Length), out var expected))
        {
            return SignatureCheck.MalformedHeader;
        }

        var anySecret = false;
        if (secrets is not null)
        {
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret))
                {
                    continue;
                }

                anySecret = true;
                if (Hex.FixedTimeEquals(ComputeDigest(secret, payload), expected))
                {
                    return SignatureCheck.Valid;
                }
            }
        }

        return anySecret ? SignatureCheck.Mismatch : SignatureCheck.NoSecret;
    }

    internal static byte[] ComputeDigest(string secret, byte[] payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(payload);
    }
}

internal enum SignatureCheck
{
    Valid,
    MissingPayload,
    MissingHeader,
    MalformedHeader,
    NoSecret,
    Mismatch,
}
