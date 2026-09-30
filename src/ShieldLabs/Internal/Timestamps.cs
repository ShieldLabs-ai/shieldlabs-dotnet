using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShieldLabs.Internal;

/// <summary>
/// Timestamp parsing for the two wire formats. Both results are UTC and truncated (never rounded)
/// to millisecond precision. Unparsable input returns null.
/// </summary>
internal static class Timestamps
{
    // History rows: "YYYY-MM-DD HH:MM:SS[.fff]" in UTC. A zone designator, if any, is ignored.
    private static readonly Regex HistoryPattern = new Regex(
        "^([0-9]{4}-[0-9]{2}-[0-9]{2})[ T]([0-9]{2}:[0-9]{2}:[0-9]{2})(?:\\.([0-9]{1,9}))?(Z|[+-][0-9]{2}:?[0-9]{2})?$",
        RegexOptions.CultureInvariant);

    // Webhooks and the Management API: RFC 3339 with 0 to 9 fractional digits and a zone.
    private static readonly Regex Rfc3339Pattern = new Regex(
        "^([0-9]{4}-[0-9]{2}-[0-9]{2})T([0-9]{2}:[0-9]{2}:[0-9]{2})(?:\\.([0-9]{1,9}))?(Z|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant);

    internal static DateTimeOffset? ParseHistory(string? value)
    {
        var text = PyText.Strip(value ?? string.Empty);
        if (text.Length == 0)
        {
            return null;
        }

        var match = HistoryPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return Build(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, TimeSpan.Zero);
    }

    internal static DateTimeOffset? ParseRfc3339(string? value)
    {
        var match = Rfc3339Pattern.Match(value ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var zone = match.Groups[4].Value;
        var offset = TimeSpan.Zero;
        if (zone != "Z")
        {
            var sign = zone[0] == '+' ? 1 : -1;
            var hours = int.Parse(zone.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            var minutes = int.Parse(zone.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
            offset = TimeSpan.FromMinutes(sign * ((hours * 60) + minutes));
        }

        return Build(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, offset);
    }

    private static DateTimeOffset? Build(string date, string time, string fraction, TimeSpan offset)
    {
        if (!DateTime.TryParseExact(
                date + " " + time,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return null;
        }

        var millis = fraction.Length == 0
            ? 0
            : int.Parse(fraction.Length > 3 ? fraction.Substring(0, 3) : fraction.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture);

        try
        {
            var utc = DateTime.SpecifyKind(local, DateTimeKind.Unspecified).AddMilliseconds(millis) - offset;
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
