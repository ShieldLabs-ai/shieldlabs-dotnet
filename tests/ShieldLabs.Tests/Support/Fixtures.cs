using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShieldLabs.Tests.Support;

/// <summary>Loads the shared test fixtures copied next to the test assembly.</summary>
internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "data", name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    public static string Text(string name) => File.ReadAllText(Path(name));

    /// <summary>Parses a fixture into a JsonElement that stays valid (the document is never disposed).</summary>
    public static JsonElement Json(string name) => JsonDocument.Parse(Bytes(name)).RootElement;

    public static JsonNode Node(string name) => JsonNode.Parse(Bytes(name))!;

    public static IEnumerable<JsonElement> NormalizationCases()
        => Json("normalization-cases.json").GetProperty("cases").EnumerateArray();

    public static JsonElement NormalizationCase(string name)
        => NormalizationCases().Single(c => c.GetProperty("name").GetString() == name);

    public static JsonElement SignatureVector(string name)
        => Json("webhook-signature-vectors.json").GetProperty("vectors").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == name);

    /// <summary>
    /// The Identification in the exact shape of <c>normalization-cases.json</c> "expected" objects:
    /// webhook field names, observed_at as RFC 3339 with milliseconds and Z, no raw.
    /// </summary>
    public static JsonObject Canonical(Identification identification)
    {
        var flags = new JsonObject();
        foreach (var pair in identification.DetectionFlags.ToDictionary())
        {
            flags[pair.Key] = pair.Value;
        }

        var signals = new JsonArray();
        foreach (var signal in identification.Signals)
        {
            signals.Add(new JsonObject
            {
                ["name"] = signal.Name,
                ["weight"] = signal.Weight,
                ["description"] = signal.Description,
            });
        }

        return new JsonObject
        {
            ["request_id"] = identification.RequestId,
            ["visitor_id"] = identification.VisitorId,
            ["device_id"] = identification.DeviceId,
            ["session_id"] = identification.SessionId,
            ["cookie_id"] = identification.CookieId,
            ["user_hid"] = identification.UserHid,
            ["domain"] = identification.Domain,
            ["public_ip"] = new JsonObject { ["ip"] = identification.PublicIp.Ip, ["country"] = identification.PublicIp.Country },
            ["local_ip"] = new JsonObject { ["ip"] = identification.LocalIp.Ip, ["country"] = identification.LocalIp.Country },
            ["connection_type"] = identification.ConnectionType,
            ["os"] = identification.Os,
            ["browser"] = identification.Browser,
            ["device_type"] = identification.DeviceType,
            ["traffic_source"] = new JsonObject
            {
                ["channel"] = identification.TrafficSource.Channel,
                ["referrer_domain"] = identification.TrafficSource.ReferrerDomain,
                ["landing_url"] = identification.TrafficSource.LandingUrl,
                ["click_id_type"] = identification.TrafficSource.ClickIdType,
                ["utm_source"] = identification.TrafficSource.UtmSource,
                ["utm_medium"] = identification.TrafficSource.UtmMedium,
                ["utm_campaign"] = identification.TrafficSource.UtmCampaign,
                ["utm_content"] = identification.TrafficSource.UtmContent,
                ["utm_term"] = identification.TrafficSource.UtmTerm,
            },
            ["risk_score"] = identification.RiskScore,
            ["signals"] = signals,
            ["detection_flags"] = flags,
            ["observed_at"] = FormatMillis(identification.ObservedAt),
            ["source"] = identification.Source == IdentificationSource.History ? "history" : "webhook",
        };
    }

    public static string FormatMillis(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Asserts that two JSON values are equal, printing both on failure.</summary>
    public static void AssertJsonEqual(JsonNode? expected, JsonNode? actual, string context)
    {
        if (!JsonNode.DeepEquals(expected, actual))
        {
            Assert.Fail($"{context}\nexpected: {expected?.ToJsonString()}\nactual:   {actual?.ToJsonString()}");
        }
    }
}
