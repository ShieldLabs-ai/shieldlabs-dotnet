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
    private static readonly Dictionary<string, string> HistoryFlagColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DetectionFlagNames.Vpn] = "is_vpn",
        [DetectionFlagNames.PrivacyRelay] = "is_privacy_relay",
        [DetectionFlagNames.Tor] = "is_tor",
        [DetectionFlagNames.Proxy] = "is_proxy",
        [DetectionFlagNames.DatacenterIp] = "is_datacenter",
        [DetectionFlagNames.Abuser] = "is_abuser",
        [DetectionFlagNames.OsMismatch] = "is_os_mismatch",
        [DetectionFlagNames.OsNotDetected] = "is_os_not_detected",
        [DetectionFlagNames.TimezoneMismatch] = "is_timezone_mismatch",
        [DetectionFlagNames.AntiDetectBrowser] = "is_antidetect",
        [DetectionFlagNames.BrowserAutomation] = "is_browser_automation",
        [DetectionFlagNames.Incognito] = "is_incognito",
        [DetectionFlagNames.SearchBot] = "is_search_bot",
        [DetectionFlagNames.SuspiciousPaidClick] = "is_suspicious_paid_click",
        [DetectionFlagNames.JavascriptDisabled] = "is_js_disabled",
        [DetectionFlagNames.StunNotChecked] = "is_stun_not_checked",
        [DetectionFlagNames.CheckIncomplete] = "check_incomplete",
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
        var leakSource = PyText.Strip(JsonUtil.StringOrEmpty(JsonUtil.Get(row, "webrtc_leak_source")));
        string localIp;
        string localCountry;
        if (leakSource.Length > 0 && leakSource != "none")
        {
            localIp = Ip(JsonUtil.Get(row, "webrtc_leak_ip"));
            localCountry = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "webrtc_leak_country"));
        }
        else
        {
            localIp = Ip(JsonUtil.Get(row, "web_rtc_ip"));
            localCountry = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "web_rtc_country"));
        }

        var publicIp = Ip(JsonUtil.Get(row, "ip"));

        var signals = new List<Signal>();
        var ipLeakDetail = false;
        foreach (var detail in ScoreDetails(JsonUtil.Get(row, "score_details")))
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var description = JsonUtil.Truthy(JsonUtil.Get(detail, "Description"))
                ? JsonUtil.StringOrNull(JsonUtil.Get(detail, "Description")) ?? string.Empty
                : string.Empty;
            if (description.StartsWith(IpLeakPrefix, StringComparison.Ordinal))
            {
                ipLeakDetail = true;
            }

            if (!JsonUtil.TryGetExactInt(JsonUtil.Get(detail, "Value"), out var weight) || weight == 0)
            {
                continue;
            }

            signals.Add(new Signal { Name = SignalSlug(description), Weight = weight, Description = description });
        }

        var searchBot = JsonUtil.Truthy(JsonUtil.Get(row, "is_search_bot"));
        var connectionType = JsonUtil.AsString(JsonUtil.Get(row, "connection_type"));
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in DetectionFlagNames.All)
        {
            if (name == DetectionFlagNames.BrowserVpnProxy)
            {
                flags[name] = IsExactString(JsonUtil.Get(row, "connection_type"), ConnectionTypes.BrowserVpnProxy);
            }
            else if (name == DetectionFlagNames.IpMismatch)
            {
                flags[name] = !searchBot
                    && (ipLeakDetail || (publicIp.Length > 0 && localIp.Length > 0 && publicIp != localIp));
            }
            else
            {
                flags[name] = JsonUtil.Truthy(JsonUtil.Get(row, HistoryFlagColumns[name]));
            }
        }

        var siteDomain = JsonUtil.Get(row, "site_domain");
        var domain = JsonUtil.Truthy(siteDomain) ? JsonUtil.AsString(siteDomain) : JsonUtil.AsString(JsonUtil.Get(row, "domain"));

        return new Identification
        {
            RequestId = JsonUtil.AsString(JsonUtil.Get(row, "request_id")),
            VisitorId = JsonUtil.AsString(JsonUtil.Get(row, "visitor_id")),
            DeviceId = JsonUtil.AsString(JsonUtil.Get(row, "device_id")),
            SessionId = JsonUtil.AsString(JsonUtil.Get(row, "session_id")),
            CookieId = JsonUtil.AsString(JsonUtil.Get(row, "cookie_id")),
            UserHid = UserHid(JsonUtil.Get(row, "user_hid")),
            Domain = domain,
            PublicIp = new IpInfo { Ip = publicIp, Country = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "country")) },
            LocalIp = new IpInfo { Ip = localIp, Country = localCountry },
            ConnectionType = connectionType,
            Os = JsonUtil.AsString(JsonUtil.Get(row, "os")),
            Browser = JsonUtil.AsString(JsonUtil.Get(row, "browser")),
            DeviceType = JsonUtil.AsString(JsonUtil.Get(row, "device_type")),
            TrafficSource = new TrafficSource
            {
                Channel = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "traffic_channel")),
                ReferrerDomain = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "referrer_domain")),
                LandingUrl = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "entry_url")),
                ClickIdType = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "click_id_type")),
                UtmSource = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "utm_source")),
                UtmMedium = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "utm_medium")),
                UtmCampaign = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "utm_campaign")),
                UtmContent = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "utm_content")),
                UtmTerm = JsonUtil.StringOrEmpty(JsonUtil.Get(row, "utm_term")),
            },
            RiskScore = JsonUtil.AsInt(JsonUtil.Get(row, "score")),
            Signals = signals,
            DetectionFlags = BuildFlags(flags),
            ObservedAt = Timestamps.ParseHistory(JsonUtil.StringOrNull(JsonUtil.Get(row, "created_at"))) ?? DateTimeOffset.MinValue,
            Source = IdentificationSource.History,
            Raw = row,
        };
    }

    /// <summary>Normalizes the <c>data</c> object of an <c>identification.scored</c> webhook.</summary>
    internal static Identification FromWebhookData(JsonElement data)
    {
        var flagsObject = JsonUtil.Get(data, "detection_flags");
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in DetectionFlagNames.All)
        {
            flags[name] = flagsObject is JsonElement f && f.ValueKind == JsonValueKind.Object && JsonUtil.Truthy(JsonUtil.Get(f, name));
        }

        var traffic = JsonUtil.Get(data, "traffic_source") ?? default;
        var signals = new List<Signal>();
        if (JsonUtil.Get(data, "signals") is JsonElement signalArray && signalArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in signalArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                signals.Add(new Signal
                {
                    Name = JsonUtil.AsString(JsonUtil.Get(item, "name")),
                    Weight = JsonUtil.AsInt(JsonUtil.Get(item, "weight")),
                    Description = null,
                });
            }
        }

        return new Identification
        {
            RequestId = JsonUtil.AsString(JsonUtil.Get(data, "request_id")),
            VisitorId = JsonUtil.AsString(JsonUtil.Get(data, "visitor_id")),
            DeviceId = JsonUtil.AsString(JsonUtil.Get(data, "device_id")),
            SessionId = JsonUtil.AsString(JsonUtil.Get(data, "session_id")),
            CookieId = JsonUtil.AsString(JsonUtil.Get(data, "cookie_id")),
            UserHid = UserHid(JsonUtil.Get(data, "user_hid")),
            Domain = JsonUtil.AsString(JsonUtil.Get(data, "domain")),
            PublicIp = IpObject(JsonUtil.Get(data, "public_ip")),
            LocalIp = IpObject(JsonUtil.Get(data, "local_ip")),
            ConnectionType = JsonUtil.AsString(JsonUtil.Get(data, "connection_type")),
            Os = JsonUtil.AsString(JsonUtil.Get(data, "os")),
            Browser = JsonUtil.AsString(JsonUtil.Get(data, "browser")),
            DeviceType = JsonUtil.AsString(JsonUtil.Get(data, "device_type")),
            TrafficSource = new TrafficSource
            {
                Channel = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "channel")),
                ReferrerDomain = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "referrer_domain")),
                LandingUrl = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "landing_url")),
                ClickIdType = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "click_id_type")),
                UtmSource = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "utm_source")),
                UtmMedium = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "utm_medium")),
                UtmCampaign = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "utm_campaign")),
                UtmContent = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "utm_content")),
                UtmTerm = JsonUtil.StringOrEmpty(JsonUtil.Get(traffic, "utm_term")),
            },
            RiskScore = JsonUtil.AsInt(JsonUtil.Get(data, "risk_score")),
            Signals = signals,
            DetectionFlags = BuildFlags(flags),
            ObservedAt = Timestamps.ParseRfc3339(JsonUtil.StringOrNull(JsonUtil.Get(data, "observed_at"))) ?? DateTimeOffset.MinValue,
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
            Ip = Ip(JsonUtil.Get(obj, "ip")),
            Country = JsonUtil.StringOrEmpty(JsonUtil.Get(obj, "country")),
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
