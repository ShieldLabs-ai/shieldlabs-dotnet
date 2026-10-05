using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ShieldLabs.Internal;

// The type describes the contract, not the received JSON. Keeping the original element lets
// the existing coercion/default rules handle missing, null and malformed historical values.
internal readonly struct WireField<T>
{
    internal WireField(string name) => Name = name;
    internal string Name { get; }
}

internal sealed class WireArray<T> { }

internal static class Wire
{
    internal static JsonElement? Read<T>(JsonElement value, WireField<T> field)
        => JsonUtil.Get(value, field.Name);

    internal static KeyValuePair<string, string> Parameter<T>(WireField<T> field, T value)
        => new KeyValuePair<string, string>(field.Name, System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
}
