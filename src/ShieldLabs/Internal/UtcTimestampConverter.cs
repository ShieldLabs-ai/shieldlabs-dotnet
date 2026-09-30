using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShieldLabs.Internal;

/// <summary>
/// Writes a timestamp as UTC with millisecond precision and a <c>Z</c> designator
/// (<c>2026-09-30T12:34:56.789Z</c>), the form the webhook uses and every ShieldLabs server SDK
/// writes. Reads RFC 3339 and ISO 8601 timestamps with any offset.
/// </summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadTimestamp(ref reader);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(Format(value));

    /// <summary>The value in UTC as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (milliseconds truncated, never rounded).</summary>
    internal static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ReadTimestamp(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (Timestamps.ParseRfc3339(reader.GetString()) is DateTimeOffset parsed)
            {
                return parsed;
            }

            if (reader.TryGetDateTimeOffset(out var value))
            {
                return value.ToUniversalTime();
            }
        }

        throw new JsonException("Expected a timestamp such as 2026-09-30T12:34:56.789Z.");
    }
}

/// <summary><see cref="UtcTimestampConverter"/> for nullable timestamps; null stays null.</summary>
internal sealed class NullableUtcTimestampConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : UtcTimestampConverter.ReadTimestamp(ref reader);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is DateTimeOffset timestamp)
        {
            writer.WriteStringValue(UtcTimestampConverter.Format(timestamp));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
