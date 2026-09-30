using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShieldLabs.Internal;

/// <summary>Serializes enum members as snake_case strings (<c>RateLimited</c> is <c>"rate_limited"</c>).</summary>
internal sealed class SnakeCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = BuildNames();
    private static readonly Dictionary<string, TEnum> Values = BuildValues();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value))
        {
            return value;
        }

        throw new JsonException($"Unexpected value for {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name))
        {
            throw new JsonException($"Undefined {typeof(TEnum).Name} value {value}.");
        }

        writer.WriteStringValue(name);
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static Dictionary<TEnum, string> BuildNames()
    {
        var names = new Dictionary<TEnum, string>();
        foreach (TEnum value in Enum.GetValues(typeof(TEnum)))
        {
            names[value] = ToSnakeCase(value.ToString());
        }

        return names;
    }

    private static Dictionary<string, TEnum> BuildValues()
    {
        var values = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Names)
        {
            values[pair.Value] = pair.Key;
        }

        return values;
    }
}
