using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShieldLabs;

/// <summary>
/// The 19 stable detection flags of an identification. Branch on these (and on the Risk Score);
/// a flag that is missing on the wire is false.
/// </summary>
public sealed class DetectionFlags
{
    /// <summary>A VPN was detected.</summary>
    [JsonPropertyName("vpn")]
    public bool Vpn { get; init; }

    /// <summary>A privacy relay (such as iCloud Private Relay) was detected.</summary>
    [JsonPropertyName("privacy_relay")]
    public bool PrivacyRelay { get; init; }

    /// <summary>A browser VPN or proxy extension was detected (connection type <c>browser_vpn_proxy</c>).</summary>
    [JsonPropertyName("browser_vpn_proxy")]
    public bool BrowserVpnProxy { get; init; }

    /// <summary>The Tor network was detected.</summary>
    [JsonPropertyName("tor")]
    public bool Tor { get; init; }

    /// <summary>A proxy was detected.</summary>
    [JsonPropertyName("proxy")]
    public bool Proxy { get; init; }

    /// <summary>The IP belongs to a data center.</summary>
    [JsonPropertyName("datacenter_ip")]
    public bool DatacenterIp { get; init; }

    /// <summary>The IP has an abuse history.</summary>
    [JsonPropertyName("abuser")]
    public bool Abuser { get; init; }

    /// <summary>The operating system reported by the browser does not match the network evidence.</summary>
    [JsonPropertyName("os_mismatch")]
    public bool OsMismatch { get; init; }

    /// <summary>The operating system could not be detected.</summary>
    [JsonPropertyName("os_not_detected")]
    public bool OsNotDetected { get; init; }

    /// <summary>The browser time zone does not match the IP location.</summary>
    [JsonPropertyName("timezone_mismatch")]
    public bool TimezoneMismatch { get; init; }

    /// <summary>An anti-detect browser was detected.</summary>
    [JsonPropertyName("anti_detect_browser")]
    public bool AntiDetectBrowser { get; init; }

    /// <summary>Browser automation was detected.</summary>
    [JsonPropertyName("browser_automation")]
    public bool BrowserAutomation { get; init; }

    /// <summary>The public IP differs from the local IP seen by the network check. Informational.</summary>
    [JsonPropertyName("ip_mismatch")]
    public bool IpMismatch { get; init; }

    /// <summary>The browser runs in a private window.</summary>
    [JsonPropertyName("incognito")]
    public bool Incognito { get; init; }

    /// <summary>A search engine crawler.</summary>
    [JsonPropertyName("search_bot")]
    public bool SearchBot { get; init; }

    /// <summary>A paid click (ad channel) with a Risk Score of 60 or more.</summary>
    [JsonPropertyName("suspicious_paid_click")]
    public bool SuspiciousPaidClick { get; init; }

    /// <summary>JavaScript was disabled.</summary>
    [JsonPropertyName("javascript_disabled")]
    public bool JavascriptDisabled { get; init; }

    /// <summary>The network check (STUN) could not run.</summary>
    [JsonPropertyName("stun_not_checked")]
    public bool StunNotChecked { get; init; }

    /// <summary>A browser check did not finish in time. Informational.</summary>
    [JsonPropertyName("check_incomplete")]
    public bool CheckIncomplete { get; init; }

    /// <summary>
    /// Returns the value of a flag by its wire name (see <see cref="DetectionFlagNames"/>), or false
    /// for a name that is not one of the 19 flags.
    /// </summary>
    public bool IsSet(string name) => name switch
    {
        DetectionFlagNames.Vpn => Vpn,
        DetectionFlagNames.PrivacyRelay => PrivacyRelay,
        DetectionFlagNames.BrowserVpnProxy => BrowserVpnProxy,
        DetectionFlagNames.Tor => Tor,
        DetectionFlagNames.Proxy => Proxy,
        DetectionFlagNames.DatacenterIp => DatacenterIp,
        DetectionFlagNames.Abuser => Abuser,
        DetectionFlagNames.OsMismatch => OsMismatch,
        DetectionFlagNames.OsNotDetected => OsNotDetected,
        DetectionFlagNames.TimezoneMismatch => TimezoneMismatch,
        DetectionFlagNames.AntiDetectBrowser => AntiDetectBrowser,
        DetectionFlagNames.BrowserAutomation => BrowserAutomation,
        DetectionFlagNames.IpMismatch => IpMismatch,
        DetectionFlagNames.Incognito => Incognito,
        DetectionFlagNames.SearchBot => SearchBot,
        DetectionFlagNames.SuspiciousPaidClick => SuspiciousPaidClick,
        DetectionFlagNames.JavascriptDisabled => JavascriptDisabled,
        DetectionFlagNames.StunNotChecked => StunNotChecked,
        DetectionFlagNames.CheckIncomplete => CheckIncomplete,
        _ => false,
    };

    /// <summary>All 19 flags by wire name, in the contract order.</summary>
    public IReadOnlyDictionary<string, bool> ToDictionary()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in DetectionFlagNames.All)
        {
            result[name] = IsSet(name);
        }

        return result;
    }
}

/// <summary>Wire names of the 19 detection flags.</summary>
public static class DetectionFlagNames
{
    /// <summary><c>vpn</c></summary>
    public const string Vpn = "vpn";

    /// <summary><c>privacy_relay</c></summary>
    public const string PrivacyRelay = "privacy_relay";

    /// <summary><c>browser_vpn_proxy</c></summary>
    public const string BrowserVpnProxy = "browser_vpn_proxy";

    /// <summary><c>tor</c></summary>
    public const string Tor = "tor";

    /// <summary><c>proxy</c></summary>
    public const string Proxy = "proxy";

    /// <summary><c>datacenter_ip</c></summary>
    public const string DatacenterIp = "datacenter_ip";

    /// <summary><c>abuser</c></summary>
    public const string Abuser = "abuser";

    /// <summary><c>os_mismatch</c></summary>
    public const string OsMismatch = "os_mismatch";

    /// <summary><c>os_not_detected</c></summary>
    public const string OsNotDetected = "os_not_detected";

    /// <summary><c>timezone_mismatch</c></summary>
    public const string TimezoneMismatch = "timezone_mismatch";

    /// <summary><c>anti_detect_browser</c></summary>
    public const string AntiDetectBrowser = "anti_detect_browser";

    /// <summary><c>browser_automation</c></summary>
    public const string BrowserAutomation = "browser_automation";

    /// <summary><c>ip_mismatch</c></summary>
    public const string IpMismatch = "ip_mismatch";

    /// <summary><c>incognito</c></summary>
    public const string Incognito = "incognito";

    /// <summary><c>search_bot</c></summary>
    public const string SearchBot = "search_bot";

    /// <summary><c>suspicious_paid_click</c></summary>
    public const string SuspiciousPaidClick = "suspicious_paid_click";

    /// <summary><c>javascript_disabled</c></summary>
    public const string JavascriptDisabled = "javascript_disabled";

    /// <summary><c>stun_not_checked</c></summary>
    public const string StunNotChecked = "stun_not_checked";

    /// <summary><c>check_incomplete</c></summary>
    public const string CheckIncomplete = "check_incomplete";

    /// <summary>All 19 flag names in the contract order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Vpn, PrivacyRelay, BrowserVpnProxy, Tor, Proxy, DatacenterIp, Abuser, OsMismatch, OsNotDetected,
        TimezoneMismatch, AntiDetectBrowser, BrowserAutomation, IpMismatch, Incognito, SearchBot,
        SuspiciousPaidClick, JavascriptDisabled, StunNotChecked, CheckIncomplete,
    };
}

/// <summary>
/// Known risk signal names (<see cref="Signal.Name"/>). The set is open: new names can appear at any
/// time, so compare against these constants and never treat them as a closed list.
/// </summary>
public static class SignalNames
{
    /// <summary>Tor network.</summary>
    public const string Tor = "tor";

    /// <summary>JavaScript disabled.</summary>
    public const string JavascriptDisabled = "javascript_disabled";

    /// <summary>Operating system mismatch.</summary>
    public const string OsMismatch = "os_mismatch";

    /// <summary>Anti-detect browser.</summary>
    public const string AntidetectBrowser = "antidetect_browser";

    /// <summary>Network routing through a proxy that matches an anti-detect browser pattern.</summary>
    public const string ProxyRoutedAntidetect = "proxy_routed_antidetect";

    /// <summary>Carried-forward verdict of <see cref="ProxyRoutedAntidetect"/> for the same device and IP.</summary>
    public const string PortScanRoutedViaProxy = "port_scan_routed_via_proxy";

    /// <summary>Browser automation.</summary>
    public const string BrowserAutomation = "browser_automation";

    /// <summary>The network check (STUN) could not run.</summary>
    public const string StunNotChecked = "stun_not_checked";

    /// <summary>Operating system not detected.</summary>
    public const string OsNotDetected = "os_not_detected";

    /// <summary>Browser VPN or proxy extension.</summary>
    public const string BrowserVpnProxy = "browser_vpn_proxy";

    /// <summary>VPN.</summary>
    public const string Vpn = "vpn";

    /// <summary>Privacy relay.</summary>
    public const string PrivacyRelay = "privacy_relay";

    /// <summary>Proxy.</summary>
    public const string Proxy = "proxy";

    /// <summary>Data center IP.</summary>
    public const string DatacenterIp = "datacenter_ip";

    /// <summary>IP with an abuse history.</summary>
    public const string Abuser = "abuser";

    /// <summary>Browser time zone does not match the IP location.</summary>
    public const string TimezoneMismatch = "timezone_mismatch";

    /// <summary>Late network check correction (negative weight, pairs with <see cref="StunNotChecked"/>).</summary>
    public const string StunLateCorrection = "stun_late_correction";

    /// <summary>Rate-limit marker that comes with a Risk Score of 999.</summary>
    public const string RateLimited = "rate_limited";
}

/// <summary>Known values of <see cref="Identification.ConnectionType"/>. Unknown values are kept as they are.</summary>
public static class ConnectionTypes
{
    /// <summary><c>direct</c></summary>
    public const string Direct = "direct";

    /// <summary><c>mobile</c></summary>
    public const string Mobile = "mobile";

    /// <summary><c>vpn</c></summary>
    public const string Vpn = "vpn";

    /// <summary><c>proxy</c></summary>
    public const string Proxy = "proxy";

    /// <summary><c>tor</c></summary>
    public const string Tor = "tor";

    /// <summary><c>privacy_relay</c></summary>
    public const string PrivacyRelay = "privacy_relay";

    /// <summary><c>browser_vpn_proxy</c></summary>
    public const string BrowserVpnProxy = "browser_vpn_proxy";

    /// <summary><c>unknown</c></summary>
    public const string Unknown = "unknown";
}
