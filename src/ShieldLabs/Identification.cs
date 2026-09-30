using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>
/// One identification: one run of the ShieldLabs agent in one browser, with the verdict computed
/// on the server. The same shape is produced from a History API row and from the <c>data</c> of an
/// <c>identification.scored</c> webhook. JSON names follow the webhook contract.
/// </summary>
/// <remarks>
/// Branch on <see cref="RiskScore"/> (through <see cref="Risk.Band(int)"/>) and on
/// <see cref="DetectionFlags"/>. Signal names are for display and logging; never sum signal weights.
/// </remarks>
public sealed class Identification
{
    /// <summary>UUID of this identification, created in the browser and handed to your page.</summary>
    [JsonPropertyName("request_id")]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Server-side visitor identifier, sticky to the device. The nil UUID is possible.</summary>
    [JsonPropertyName("visitor_id")]
    public string VisitorId { get; init; } = string.Empty;

    /// <summary>
    /// Server-side device identifier that survives cleared cookies and private windows. The nil UUID
    /// <c>00000000-0000-0000-0000-000000000000</c> means "no usable device signals".
    /// </summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>One visit on one origin. The nil UUID is possible.</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>First-party browser identifier kept by the agent. The nil UUID is possible.</summary>
    [JsonPropertyName("cookie_id")]
    public string CookieId { get; init; } = string.Empty;

    /// <summary>
    /// Your hashed or pseudonymous account identifier passed to the agent. <c>"anonymous"</c> for
    /// anonymous checks; other values such as <c>"fail"</c>, <c>"-1"</c> and <c>"unknown"</c> are kept
    /// as they are. Null when no User HID was stored.
    /// </summary>
    [JsonPropertyName("user_hid")]
    public string? UserHid { get; init; }

    /// <summary>The registered domain the identification belongs to.</summary>
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Public IP of the HTTP request (IPv4) and its country.</summary>
    [JsonPropertyName("public_ip")]
    public IpInfo PublicIp { get; init; } = new IpInfo();

    /// <summary>Local IP seen by the network check (IPv4) and its country.</summary>
    [JsonPropertyName("local_ip")]
    public IpInfo LocalIp { get; init; } = new IpInfo();

    /// <summary>
    /// Connection type. Known values are listed in <see cref="ConnectionTypes"/>; unknown values
    /// are kept as they are.
    /// </summary>
    [JsonPropertyName("connection_type")]
    public string ConnectionType { get; init; } = string.Empty;

    /// <summary>Operating system, for example <c>Windows</c>, <c>Mac OS X</c>, <c>Android</c>.</summary>
    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;

    /// <summary>Browser, for example <c>Chrome</c>, <c>Safari</c>, <c>Firefox</c>.</summary>
    [JsonPropertyName("browser")]
    public string Browser { get; init; } = string.Empty;

    /// <summary><c>desktop</c>, <c>mobile</c>, <c>tablet</c> or <c>unknown</c>.</summary>
    [JsonPropertyName("device_type")]
    public string DeviceType { get; init; } = string.Empty;

    /// <summary>Where the visit came from. Every field is an empty string when absent.</summary>
    [JsonPropertyName("traffic_source")]
    public TrafficSource TrafficSource { get; init; } = new TrafficSource();

    /// <summary>
    /// Risk Score, an integer from 0 to 100. A value above 100 (999) is a rate-limit marker, never a
    /// score: check <see cref="Risk.IsRateLimited(int)"/> first.
    /// </summary>
    [JsonPropertyName("risk_score")]
    public int RiskScore { get; init; }

    /// <summary>
    /// The weighted risk signals behind the score, in server order. Names can repeat and weights can
    /// be negative. Known names are listed in <see cref="SignalNames"/>; the set is open.
    /// </summary>
    [JsonPropertyName("signals")]
    public IReadOnlyList<Signal> Signals { get; init; } = Array.Empty<Signal>();

    /// <summary>The 19 detection flags. A flag the server did not send is false.</summary>
    [JsonPropertyName("detection_flags")]
    public DetectionFlags DetectionFlags { get; init; } = new DetectionFlags();

    /// <summary>
    /// When the identification was observed, in UTC with millisecond precision: the webhook
    /// <c>observed_at</c> or the History <c>created_at</c>. <see cref="DateTimeOffset.MinValue"/> when
    /// the server sent no parsable timestamp (such an identification is always stale for
    /// <see cref="Risk.Evaluate"/>). Serialized as <c>2026-09-30T12:34:56.789Z</c>.
    /// </summary>
    [JsonPropertyName("observed_at")]
    [JsonConverter(typeof(UtcTimestampConverter))]
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>Which surface this identification was read from.</summary>
    [JsonPropertyName("source")]
    public IdentificationSource Source { get; init; }

    /// <summary>
    /// The original JSON object: the webhook <c>data</c> or the History row, including fields this
    /// model does not map.
    /// </summary>
    [JsonPropertyName("raw")]
    public JsonElement Raw { get; init; } = JsonUtil.EmptyObject;
}

/// <summary>An IPv4 address and the English name of its country (for example <c>Germany</c>).</summary>
public sealed class IpInfo
{
    /// <summary>Dotted IPv4 address, or an empty string when unknown (IPv6 visitors appear as empty).</summary>
    [JsonPropertyName("ip")]
    public string Ip { get; init; } = string.Empty;

    /// <summary>English country name, for example <c>United States</c>, or an empty string.</summary>
    [JsonPropertyName("country")]
    public string Country { get; init; } = string.Empty;
}

/// <summary>Traffic attribution of the visit. Every value is an empty string when absent.</summary>
public sealed class TrafficSource
{
    /// <summary>Channel, for example <c>Google Ads</c>, <c>Organic Search</c>, <c>Referral</c>, <c>Direct</c>.</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    /// <summary>Registrable domain of the referrer without <c>www.</c>, or the crawler name for search bots.</summary>
    [JsonPropertyName("referrer_domain")]
    public string ReferrerDomain { get; init; } = string.Empty;

    /// <summary>Landing URL without the fragment. It may contain query-string data.</summary>
    [JsonPropertyName("landing_url")]
    public string LandingUrl { get; init; } = string.Empty;

    /// <summary>Click ID type: <c>gclid</c>, <c>gbraid</c>, <c>wbraid</c>, <c>msclkid</c>, <c>ttclid</c>, <c>fbclid</c>.</summary>
    [JsonPropertyName("click_id_type")]
    public string ClickIdType { get; init; } = string.Empty;

    /// <summary>UTM source, lowercased.</summary>
    [JsonPropertyName("utm_source")]
    public string UtmSource { get; init; } = string.Empty;

    /// <summary>UTM medium, lowercased.</summary>
    [JsonPropertyName("utm_medium")]
    public string UtmMedium { get; init; } = string.Empty;

    /// <summary>UTM campaign, as sent.</summary>
    [JsonPropertyName("utm_campaign")]
    public string UtmCampaign { get; init; } = string.Empty;

    /// <summary>UTM content, as sent.</summary>
    [JsonPropertyName("utm_content")]
    public string UtmContent { get; init; } = string.Empty;

    /// <summary>UTM term, as sent.</summary>
    [JsonPropertyName("utm_term")]
    public string UtmTerm { get; init; } = string.Empty;
}

/// <summary>One weighted risk signal behind a Risk Score.</summary>
public sealed class Signal
{
    /// <summary>Stable signal name (slug), for example <c>vpn</c>. See <see cref="SignalNames"/>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Weight of the signal. Can be negative; never sum weights yourself.</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; init; }

    /// <summary>
    /// Human-readable description from a History row, or null for webhook signals. Free text: use it
    /// for display and logging only.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Where an <see cref="Identification"/> was read from.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<IdentificationSource>))]
public enum IdentificationSource
{
    /// <summary>The <c>data</c> of an <c>identification.scored</c> webhook (<c>"webhook"</c>).</summary>
    Webhook,

    /// <summary>A History API row (<c>"history"</c>).</summary>
    History,
}
