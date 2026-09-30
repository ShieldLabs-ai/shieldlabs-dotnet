using System;
using System.Globalization;
using System.Text.Json;

namespace ShieldLabs.Internal;

/// <summary>
/// Defensive JSON readers. They mirror the semantics of a dictionary lookup with a default value,
/// so a missing key, a JSON null or a value of an unexpected type never throws.
/// </summary>
internal static class JsonUtil
{
    private static readonly JsonReaderOptions ReaderOptions = new JsonReaderOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

#if !NET8_0_OR_GREATER
    // Declared before EmptyObject: static fields initialize in textual order.
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, true);
#endif

    /// <summary>An empty JSON object, used as the default value of <c>Raw</c> properties.</summary>
    internal static readonly JsonElement EmptyObject = Parse(new byte[] { (byte)'{', (byte)'}' });

    /// <summary>
    /// Parses one JSON document into a <see cref="JsonElement"/> that does not need disposal and
    /// stays valid for as long as it is referenced. Rejects invalid UTF-8 and trailing content.
    /// A UTF-8 byte order mark is skipped.
    /// </summary>
    /// <exception cref="JsonException">The input is not a single valid JSON value.</exception>
    internal static JsonElement Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8.Slice(3);
        }

        if (!IsValidUtf8(utf8))
        {
            throw new JsonException("The JSON text is not valid UTF-8.");
        }

        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        var element = JsonElement.ParseValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Unexpected content after the JSON value.");
        }

        return element;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> utf8)
    {
#if NET8_0_OR_GREATER
        return System.Text.Unicode.Utf8.IsValid(utf8);
#else
        try
        {
            var bytes = utf8.ToArray();
            StrictUtf8.GetCharCount(bytes, 0, bytes.Length);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
#endif
    }

    /// <summary>Returns the property value, or null when <paramref name="obj"/> is not an object or has no such key.</summary>
    internal static JsonElement? Get(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>Truthiness of a JSON value: false for missing, null, false, 0, "", [] and {}.</summary>
    internal static bool Truthy(JsonElement? value)
    {
        if (value is not JsonElement v)
        {
            return false;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.Number:
                return v.TryGetDouble(out var d) ? d != 0 : true;
            case JsonValueKind.String:
                return ReadString(v).Length > 0;
            case JsonValueKind.Array:
                return v.GetArrayLength() > 0;
            case JsonValueKind.Object:
                foreach (var _ in v.EnumerateObject())
                {
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Converts a JSON value to a string: strings as they are, numbers and booleans as their JSON
    /// text, anything else (missing, null, objects, arrays) as an empty string.
    /// </summary>
    internal static string AsString(JsonElement? value)
    {
        if (value is not JsonElement v)
        {
            return string.Empty;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                return ReadString(v);
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return v.GetRawText();
            default:
                return string.Empty;
        }
    }

    /// <summary>The value as a string when it is truthy, otherwise an empty string (<c>value or ""</c>).</summary>
    internal static string StringOrEmpty(JsonElement? value) => Truthy(value) ? AsString(value) : string.Empty;

    /// <summary>The string value, or null when the value is missing or not a JSON string.</summary>
    internal static string? StringOrNull(JsonElement? value)
        => value is JsonElement v && v.ValueKind == JsonValueKind.String ? ReadString(v) : null;

    /// <summary>
    /// The text of a JSON string value. <see cref="JsonElement.GetString"/> throws for a string with
    /// an escaped unpaired surrogate (for example <c>"\ud800"</c>); such a string is returned as its
    /// escaped JSON text without the quotes instead, so parsing never fails on it.
    /// </summary>
    internal static string ReadString(JsonElement value)
    {
        try
        {
            return value.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            var raw = value.GetRawText();
            return raw.Length >= 2 ? raw.Substring(1, raw.Length - 2) : string.Empty;
        }
    }

    /// <summary>
    /// Reads an integer. Whole numbers are returned as is; a fractional number is truncated toward
    /// zero; anything that is not a number (or does not fit in 32 bits) returns <paramref name="fallback"/>.
    /// </summary>
    internal static int AsInt(JsonElement? value, int fallback = 0)
    {
        if (value is not JsonElement v || v.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        if (v.TryGetInt32(out var i))
        {
            return i;
        }

        if (v.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            var truncated = Math.Truncate(d);
            if (truncated >= int.MinValue && truncated <= int.MaxValue)
            {
                return (int)truncated;
            }
        }

        return fallback;
    }

    /// <summary>Reads a 64-bit integer with the same rules as <see cref="AsInt"/>.</summary>
    internal static long AsLong(JsonElement? value, long fallback = 0)
    {
        if (value is not JsonElement v || v.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        if (v.TryGetInt64(out var l))
        {
            return l;
        }

        if (v.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            var truncated = Math.Truncate(d);
            // (double)long.MaxValue is 2^63, which does not fit in a long: compare with <.
            if (truncated >= long.MinValue && truncated < long.MaxValue)
            {
                return (long)truncated;
            }
        }

        return fallback;
    }

    /// <summary>True when the value is a JSON number written as a whole number that fits in 32 bits.</summary>
    internal static bool TryGetExactInt(JsonElement? value, out int result)
    {
        result = 0;
        return value is JsonElement v && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out result);
    }

    internal static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
