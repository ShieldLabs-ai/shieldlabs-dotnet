using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ShieldLabs.Internal;

/// <summary>
/// Normalizes History API rows and webhook <c>data</c> objects into one <see cref="Identification"/>.
/// Implements the normalization rules shared by every ShieldLabs server SDK; the shared test
/// fixtures pin its exact output.
/// </summary>
internal static class Normalizer
{
    internal const string NilUuid = "00000000-0000-0000-0000-000000000000";

    private const string IpLeakPrefix = "IP ≠ leakIP";
    private const string StickyPrefix = "Sticky verdict: ";

    private static readonly Dictionary<string, string> ExactSlugs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Is tor"] = "tor",
        ["Is VPN"] = "vpn",
        ["Is privacy relay"] = "privacy_relay",
        ["Is proxy"] = "proxy",
        ["Is datacenter"] = "datacenter_ip",
        ["Is abuser"] = "abuser",
        ["Stun is not checked"] = "stun_not_checked",
        ["Stun passed (late arrival, corrected)"] = "stun_late_correction",
        ["UA OS is not detected"] = "os_not_detected",
        ["Network OS is not detected"] = "os_not_detected",
        ["Browser timezone ≠ IP-timezone"] = "timezone_mismatch",
        ["Browser VPN/Proxy"] = "browser_vpn_proxy",
        ["Browser Automation"] = "browser_automation",
        ["Port scan routed via proxy (antidetect browser pattern)"] = "proxy_routed_antidetect",
        ["User has been banned 1H, to many requests"] = "rate_limited",
    };

    private static readonly KeyValuePair<string, string>[] PrefixSlugs =
    {
        new KeyValuePair<string, string>("Antidetect browser", "antidetect_browser"),
        new KeyValuePair<string, string>("Os_mismatch", "os_mismatch"),
        new KeyValuePair<string, string>("OS mismatch2", "os_mismatch2"),
        new KeyValuePair<string, string>("TCP handshake", "tcp_handshake_v2"),
        new KeyValuePair<string, string>("Latency test", "ws_tcp_latency"),
        new KeyValuePair<string, string>("JavaScript disabled", "javascript_disabled"),
    };

    // Normalized flag name -> History row column.
    private static readonly Dictionary<string, WireField<bool>> HistoryFlagColumns = new Dictionary<string, WireField<bool>>(StringComparer.Ordinal)
    {
        [DetectionFlagNames.Vpn] = WireHistoryRow.IsVpn,
        [DetectionFlagNames.PrivacyRelay] = WireHistoryRow.IsPrivacyRelay,
        [DetectionFlagNames.Tor] = WireHistoryRow.IsTor,
        [DetectionFlagNames.Proxy] = WireHistoryRow.IsProxy,
        [DetectionFlagNames.DatacenterIp] = WireHistoryRow.IsDatacenter,
        [DetectionFlagNames.Abuser] = WireHistoryRow.IsAbuser,
        [DetectionFlagNames.OsMismatch] = WireHistoryRow.IsOsMismatch,
        [DetectionFlagNames.OsNotDetected] = WireHistoryRow.IsOsNotDetected,
        [DetectionFlagNames.TimezoneMismatch] = WireHistoryRow.IsTimezoneMismatch,
        [DetectionFlagNames.AntiDetectBrowser] = WireHistoryRow.IsAntidetect,
        [DetectionFlagNames.BrowserAutomation] = WireHistoryRow.IsBrowserAutomation,
        [DetectionFlagNames.Incognito] = WireHistoryRow.IsIncognito,
        [DetectionFlagNames.SearchBot] = WireHistoryRow.IsSearchBot,
        [DetectionFlagNames.SuspiciousPaidClick] = WireHistoryRow.IsSuspiciousPaidClick,
        [DetectionFlagNames.JavascriptDisabled] = WireHistoryRow.IsJsDisabled,
        [DetectionFlagNames.StunNotChecked] = WireHistoryRow.IsStunNotChecked,
        [DetectionFlagNames.CheckIncomplete] = WireHistoryRow.CheckIncomplete,
    };

    private static readonly Dictionary<string, WireField<bool>> WebhookFlagColumns = new Dictionary<string, WireField<bool>>(StringComparer.Ordinal)
    {
        [DetectionFlagNames.Vpn] = WireDetectionFlags.Vpn,
        [DetectionFlagNames.PrivacyRelay] = WireDetectionFlags.PrivacyRelay,
        [DetectionFlagNames.BrowserVpnProxy] = WireDetectionFlags.BrowserVpnProxy,
        [DetectionFlagNames.Tor] = WireDetectionFlags.Tor,
        [DetectionFlagNames.Proxy] = WireDetectionFlags.Proxy,
        [DetectionFlagNames.DatacenterIp] = WireDetectionFlags.DatacenterIp,
        [DetectionFlagNames.Abuser] = WireDetectionFlags.Abuser,
        [DetectionFlagNames.OsMismatch] = WireDetectionFlags.OsMismatch,
        [DetectionFlagNames.OsNotDetected] = WireDetectionFlags.OsNotDetected,
        [DetectionFlagNames.TimezoneMismatch] = WireDetectionFlags.TimezoneMismatch,
        [DetectionFlagNames.AntiDetectBrowser] = WireDetectionFlags.AntiDetectBrowser,
        [DetectionFlagNames.BrowserAutomation] = WireDetectionFlags.BrowserAutomation,
        [DetectionFlagNames.IpMismatch] = WireDetectionFlags.IpMismatch,
        [DetectionFlagNames.Incognito] = WireDetectionFlags.Incognito,
        [DetectionFlagNames.SearchBot] = WireDetectionFlags.SearchBot,
        [DetectionFlagNames.SuspiciousPaidClick] = WireDetectionFlags.SuspiciousPaidClick,
        [DetectionFlagNames.JavascriptDisabled] = WireDetectionFlags.JavascriptDisabled,
        [DetectionFlagNames.StunNotChecked] = WireDetectionFlags.StunNotChecked,
        [DetectionFlagNames.CheckIncomplete] = WireDetectionFlags.CheckIncomplete,
    };

    /// <summary>
    /// Signal slug for a score detail description: exact map, then known prefixes, then the
    /// <c>Sticky verdict: </c> prefix, then the fallback slugger.
    /// </summary>
    internal static string SignalSlug(string description)
    {
        if (ExactSlugs.TryGetValue(description, out var exact))
        {
            return exact;
        }

        foreach (var pair in PrefixSlugs)
        {
            if (description.StartsWith(pair.Key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        if (description.StartsWith(StickyPrefix, StringComparison.Ordinal))
        {
            var colon = description.IndexOf(':');
            var rest = colon >= 0 && colon + 1 < description.Length
                ? PyText.Strip(description.Substring(colon + 1))
                : description;
            return FallbackSlug(rest);
        }

        return FallbackSlug(description);
    }

    /// <summary>
    /// Text before the first <c>(</c>, lowercased; spaces, <c>-</c> and <c>/</c> become one <c>_</c>;
    /// <c>≠</c> becomes <c>_neq_</c>; other characters that are not letters or digits are dropped.
    /// </summary>
    internal static string FallbackSlug(string description)
    {
        var text = PyText.Strip(description);
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = PyText.Strip(text.Substring(0, paren));
        }

        var output = new StringBuilder(text.Length + 8);
        var previousSeparator = false;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == ' ' || c == '-' || c == '/')
            {
                if (!previousSeparator && output.Length > 0)
                {
                    output.Append('_');
                    previousSeparator = true;
                }

                index++;
            }
            else if (c == '≠')
            {
                output.Append("_neq_");
                previousSeparator = false;
                index++;
            }
            else
            {
                index += PyText.AppendLowerLetterOrDigit(text, index, output, out var appended);
                if (appended)
                {
                    previousSeparator = false;
                }
            }
        }

        var slug = output.ToString().Trim('_');
        return slug.Length == 0 ? "unknown" : slug;
    }

    /// <summary>Normalizes one History API row.</summary>
    internal static Identification FromHistoryRow(JsonElement row)
    {
        var leakSource = PyText.Strip(JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.WebrtcLeakSource)));
        string localIp;
        string localCountry;
        if (leakSource.Length > 0 && leakSource != "none")
        {
            localIp = Ip(Wire.Read<string>(row, WireHistoryRow.WebrtcLeakIp));
            localCountry = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.WebrtcLeakCountry));
        }
        else
        {
            localIp = Ip(Wire.Read<string>(row, WireHistoryRow.WebRtcIp));
            localCountry = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.WebRtcCountry));
        }

        var publicIp = Ip(Wire.Read<string>(row, WireHistoryRow.Ip));

        var signals = new List<Signal>();
        var ipLeakDetail = false;
        foreach (var detail in ScoreDetails(Wire.Read<string>(row, WireHistoryRow.ScoreDetails)))
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var description = JsonUtil.Truthy(Wire.Read<string>(detail, WireScoreDetail.Description))
                ? JsonUtil.StringOrNull(Wire.Read<string>(detail, WireScoreDetail.Description)) ?? string.Empty
                : string.Empty;
            if (description.StartsWith(IpLeakPrefix, StringComparison.Ordinal))
            {
                ipLeakDetail = true;
            }

            if (!JsonUtil.TryGetExactInt(Wire.Read<long>(detail, WireScoreDetail.Value), out var weight) || weight == 0)
            {
                continue;
            }

            signals.Add(new Signal { Name = SignalSlug(description), Weight = weight, Description = description });
        }

        var searchBot = JsonUtil.Truthy(Wire.Read<bool>(row, WireHistoryRow.IsSearchBot));
        var connectionType = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.ConnectionType));
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in DetectionFlagNames.All)
        {
            if (name == DetectionFlagNames.BrowserVpnProxy)
            {
                flags[name] = IsExactString(Wire.Read<string>(row, WireHistoryRow.ConnectionType), ConnectionTypes.BrowserVpnProxy);
            }
            else if (name == DetectionFlagNames.IpMismatch)
            {
                flags[name] = !searchBot
                    && (ipLeakDetail || (publicIp.Length > 0 && localIp.Length > 0 && publicIp != localIp));
            }
            else
            {
                flags[name] = JsonUtil.Truthy(Wire.Read<bool>(row, HistoryFlagColumns[name]));
            }
        }

        var siteDomain = Wire.Read<string>(row, WireHistoryRow.SiteDomain);
        var domain = JsonUtil.Truthy(siteDomain) ? JsonUtil.AsString(siteDomain) : JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.Domain));

        return new Identification
        {
            RequestId = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.RequestId)),
            VisitorId = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.VisitorId)),
            DeviceId = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.DeviceId)),
            SessionId = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.SessionId)),
            CookieId = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.CookieId)),
            UserHid = UserHid(Wire.Read<string>(row, WireHistoryRow.UserHid)),
            Domain = domain,
            PublicIp = new IpInfo { Ip = publicIp, Country = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.Country)) },
            LocalIp = new IpInfo { Ip = localIp, Country = localCountry },
            ConnectionType = connectionType,
            Os = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.Os)),
            Browser = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.Browser)),
            DeviceType = JsonUtil.AsString(Wire.Read<string>(row, WireHistoryRow.DeviceType)),
            TrafficSource = new TrafficSource
            {
                Channel = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.TrafficChannel)),
                ReferrerDomain = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.ReferrerDomain)),
                LandingUrl = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.EntryUrl)),
                ClickIdType = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.ClickIdType)),
                UtmSource = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.UtmSource)),
                UtmMedium = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.UtmMedium)),
                UtmCampaign = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.UtmCampaign)),
                UtmContent = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.UtmContent)),
                UtmTerm = JsonUtil.StringOrEmpty(Wire.Read<string>(row, WireHistoryRow.UtmTerm)),
            },
            RiskScore = JsonUtil.AsInt(Wire.Read<long>(row, WireHistoryRow.Score)),
            Signals = signals,
            DetectionFlags = BuildFlags(flags),
            ObservedAt = Timestamps.ParseHistory(JsonUtil.StringOrNull(Wire.Read<string>(row, WireHistoryRow.CreatedAt))) ?? DateTimeOffset.MinValue,
            Source = IdentificationSource.History,
            Raw = row,
        };
    }

    /// <summary>Normalizes the <c>data</c> object of an <c>identification.scored</c> webhook.</summary>
    internal static Identification FromWebhookData(JsonElement data)
    {
        var flagsObject = Wire.Read<WireDetectionFlags>(data, WireIdentificationScoredData.DetectionFlags);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in DetectionFlagNames.All)
        {
            flags[name] = flagsObject is JsonElement f && f.ValueKind == JsonValueKind.Object && JsonUtil.Truthy(Wire.Read<bool>(f, WebhookFlagColumns[name]));
        }

        var traffic = Wire.Read<WireTrafficSource>(data, WireIdentificationScoredData.TrafficSource) ?? default;
        var signals = new List<Signal>();
        if (Wire.Read<WireArray<WireSignal>>(data, WireIdentificationScoredData.Signals) is JsonElement signalArray && signalArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in signalArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                signals.Add(new Signal
                {
                    Name = JsonUtil.AsString(Wire.Read<string>(item, WireSignal.Name)),
                    Weight = JsonUtil.AsInt(Wire.Read<long>(item, WireSignal.Weight)),
                    Description = null,
                });
            }
        }

        return new Identification
        {
            RequestId = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.RequestId)),
            VisitorId = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.VisitorId)),
            DeviceId = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.DeviceId)),
            SessionId = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.SessionId)),
            CookieId = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.CookieId)),
            UserHid = UserHid(Wire.Read<string>(data, WireIdentificationScoredData.UserHid)),
            Domain = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.Domain)),
            PublicIp = IpObject(Wire.Read<WireIpInfo>(data, WireIdentificationScoredData.PublicIp)),
            LocalIp = IpObject(Wire.Read<WireIpInfo>(data, WireIdentificationScoredData.LocalIp)),
            ConnectionType = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.ConnectionType)),
            Os = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.Os)),
            Browser = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.Browser)),
            DeviceType = JsonUtil.AsString(Wire.Read<string>(data, WireIdentificationScoredData.DeviceType)),
            TrafficSource = new TrafficSource
            {
                Channel = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.Channel)),
                ReferrerDomain = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.ReferrerDomain)),
                LandingUrl = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.LandingUrl)),
                ClickIdType = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.ClickIdType)),
                UtmSource = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.UtmSource)),
                UtmMedium = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.UtmMedium)),
                UtmCampaign = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.UtmCampaign)),
                UtmContent = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.UtmContent)),
                UtmTerm = JsonUtil.StringOrEmpty(Wire.Read<string>(traffic, WireTrafficSource.UtmTerm)),
            },
            RiskScore = JsonUtil.AsInt(Wire.Read<long>(data, WireIdentificationScoredData.RiskScore)),
            Signals = signals,
            DetectionFlags = BuildFlags(flags),
            ObservedAt = Timestamps.ParseRfc3339(JsonUtil.StringOrNull(Wire.Read<string>(data, WireIdentificationScoredData.ObservedAt))) ?? DateTimeOffset.MinValue,
            Source = IdentificationSource.Webhook,
            Raw = data,
        };
    }

    private static IEnumerable<JsonElement> ScoreDetails(JsonElement? value)
    {
        // score_details is a JSON-encoded string; "" or invalid JSON means no details.
        if (value is not JsonElement v || v.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        var text = JsonUtil.ReadString(v);
        if (text.Length == 0 || text[0] == '﻿')
        {
            yield break;
        }

        JsonElement parsed;
        try
        {
            parsed = JsonUtil.Parse(Encoding.UTF8.GetBytes(text));
        }
        catch (JsonException)
        {
            yield break;
        }

        if (parsed.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in parsed.EnumerateArray())
        {
            yield return item;
        }
    }

    private static string Ip(JsonElement? value)
    {
        var text = PyText.Strip(JsonUtil.StringOrEmpty(value));
        return text == "0.0.0.0" ? string.Empty : text;
    }

    private static IpInfo IpObject(JsonElement? value)
    {
        var obj = value ?? default;
        return new IpInfo
        {
            Ip = Ip(Wire.Read<string>(obj, WireIpInfo.Ip)),
            Country = JsonUtil.StringOrEmpty(Wire.Read<string>(obj, WireIpInfo.Country)),
        };
    }

    private static string? UserHid(JsonElement? value)
    {
        if (value is not JsonElement v || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            var text = JsonUtil.ReadString(v);
            return text.Length == 0 ? null : text;
        }

        var raw = JsonUtil.AsString(v);
        return raw.Length == 0 ? v.GetRawText() : raw;
    }

    private static bool IsExactString(JsonElement? value, string expected)
        => value is JsonElement v && v.ValueKind == JsonValueKind.String && JsonUtil.ReadString(v) == expected;

    private static DetectionFlags BuildFlags(Dictionary<string, bool> flags) => new DetectionFlags
    {
        Vpn = flags[DetectionFlagNames.Vpn],
        PrivacyRelay = flags[DetectionFlagNames.PrivacyRelay],
        BrowserVpnProxy = flags[DetectionFlagNames.BrowserVpnProxy],
        Tor = flags[DetectionFlagNames.Tor],
        Proxy = flags[DetectionFlagNames.Proxy],
        DatacenterIp = flags[DetectionFlagNames.DatacenterIp],
        Abuser = flags[DetectionFlagNames.Abuser],
        OsMismatch = flags[DetectionFlagNames.OsMismatch],
        OsNotDetected = flags[DetectionFlagNames.OsNotDetected],
        TimezoneMismatch = flags[DetectionFlagNames.TimezoneMismatch],
        AntiDetectBrowser = flags[DetectionFlagNames.AntiDetectBrowser],
        BrowserAutomation = flags[DetectionFlagNames.BrowserAutomation],
        IpMismatch = flags[DetectionFlagNames.IpMismatch],
        Incognito = flags[DetectionFlagNames.Incognito],
        SearchBot = flags[DetectionFlagNames.SearchBot],
        SuspiciousPaidClick = flags[DetectionFlagNames.SuspiciousPaidClick],
        JavascriptDisabled = flags[DetectionFlagNames.JavascriptDisabled],
        StunNotChecked = flags[DetectionFlagNames.StunNotChecked],
        CheckIncomplete = flags[DetectionFlagNames.CheckIncomplete],
    };
}
