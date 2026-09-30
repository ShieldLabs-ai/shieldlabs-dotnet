using System.Text.Json;
using System.Text.Json.Nodes;
using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class NormalizationTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixtures.NormalizationCases())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Fact]
    public void Fixture_covers_both_sources()
    {
        var sources = Fixtures.NormalizationCases().Select(c => c.GetProperty("source").GetString()).ToList();
        Assert.Equal(5, sources.Count(s => s == "history"));
        Assert.Equal(3, sources.Count(s => s == "webhook"));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Produces_the_expected_identification(string name)
    {
        var c = Fixtures.NormalizationCase(name);
        var input = c.GetProperty("input");
        var identification = c.GetProperty("source").GetString() == "history"
            ? Normalizer.FromHistoryRow(input)
            : Normalizer.FromWebhookData(input);

        var expected = JsonNode.Parse(c.GetProperty("expected").GetRawText());
        Fixtures.AssertJsonEqual(expected, Fixtures.Canonical(identification), name);
        Assert.Equal(input.GetRawText(), identification.Raw.GetRawText());
        Assert.Equal(19, identification.DetectionFlags.ToDictionary().Count);
        Assert.Equal(TimeSpan.Zero, identification.ObservedAt.Offset);
    }

    [Fact]
    public void Observed_at_is_truncated_to_milliseconds_not_rounded()
    {
        var identification = Normalizer.FromWebhookData(Fixtures.NormalizationCase("webhook_scored").GetProperty("input"));

        // 2026-09-30T12:34:57.482913041Z keeps .482, never .483
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 57, 482, TimeSpan.Zero), identification.ObservedAt);
    }

    [Fact]
    public void History_raw_keeps_diagnostic_fields()
    {
        var identification = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_02f1d973").GetProperty("input"));

        Assert.Equal(1790771696123, identification.Raw.GetProperty("ver").GetInt64());
        Assert.Equal("Paid Search", identification.Raw.GetProperty("traffic_channel_group").GetString());
    }

    [Theory]
    [InlineData("2026-09-30 12:34:56", "2026-09-30T12:34:56.000Z")]
    [InlineData("2026-09-30 12:34:56.1", "2026-09-30T12:34:56.100Z")]
    [InlineData("2026-09-30 12:34:56.123456789", "2026-09-30T12:34:56.123Z")]
    [InlineData("2026-09-30T12:34:56.999Z", "2026-09-30T12:34:56.999Z")]
    [InlineData("  2026-09-30 12:34:56.5  ", "2026-09-30T12:34:56.500Z")]
    public void Parses_history_timestamps(string input, string expected)
    {
        var parsed = Timestamps.ParseHistory(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, Fixtures.FormatMillis(parsed!.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-09-30")]
    [InlineData("2026-13-30 12:34:56")]
    [InlineData("2026-09-30 25:00:00")]
    [InlineData("30/09/2026 12:34:56")]
    [InlineData(null)]
    public void Rejects_invalid_history_timestamps(string? input)
    {
        Assert.Null(Timestamps.ParseHistory(input));
    }

    [Theory]
    [InlineData("2026-09-30T12:34:57Z", "2026-09-30T12:34:57.000Z")]
    [InlineData("2026-09-30T12:34:57.5Z", "2026-09-30T12:34:57.500Z")]
    [InlineData("2026-09-30T12:34:57.482913041Z", "2026-09-30T12:34:57.482Z")]
    [InlineData("2026-09-30T14:34:57.100+02:00", "2026-09-30T12:34:57.100Z")]
    [InlineData("2026-09-30T08:04:57-04:30", "2026-09-30T12:34:57.000Z")]
    public void Parses_rfc3339_timestamps(string input, string expected)
    {
        var parsed = Timestamps.ParseRfc3339(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, Fixtures.FormatMillis(parsed!.Value));
    }

    [Theory]
    [InlineData("2026-09-30 12:34:57Z")]
    [InlineData("2026-09-30T12:34:57")]
    [InlineData("2026-09-30T12:34:57.1234567891Z")]
    [InlineData(" 2026-09-30T12:34:57Z")]
    [InlineData("0001-01-01T00:00:00+01:00")]
    public void Rejects_invalid_rfc3339_timestamps(string input)
    {
        Assert.Null(Timestamps.ParseRfc3339(input));
    }

    [Fact]
    public void Tolerates_missing_and_mistyped_fields()
    {
        var row = JsonDocument.Parse("""
            {
              "request_id": "7c1e2f4a-3b6d-4e8f-9a0b-1c2d3e4f5a6b",
              "score": "80",
              "score_details": "{not json",
              "created_at": 12345,
              "is_vpn": 1,
              "is_tor": "yes",
              "is_proxy": null,
              "user_hid": null,
              "site_domain": "",
              "domain": "shop.example.com",
              "ip": "0.0.0.0"
            }
            """).RootElement;

        var identification = Normalizer.FromHistoryRow(row);

        Assert.Equal("7c1e2f4a-3b6d-4e8f-9a0b-1c2d3e4f5a6b", identification.RequestId);
        Assert.Equal(string.Empty, identification.VisitorId);
        Assert.Equal(0, identification.RiskScore);
        Assert.Empty(identification.Signals);
        Assert.Equal(DateTimeOffset.MinValue, identification.ObservedAt);
        Assert.True(identification.DetectionFlags.Vpn);
        Assert.True(identification.DetectionFlags.Tor);
        Assert.False(identification.DetectionFlags.Proxy);
        Assert.Null(identification.UserHid);
        Assert.Equal("shop.example.com", identification.Domain);
        Assert.Equal(string.Empty, identification.PublicIp.Ip);
        Assert.Equal(IdentificationSource.History, identification.Source);
    }

    [Fact]
    public void Score_details_keep_only_integer_non_zero_values_in_order()
    {
        var details = "[{\"Value\":10,\"Description\":\"Is proxy\"},{\"Value\":0,\"Description\":\"Check Incomplete\"},"
            + "{\"Value\":10.5,\"Description\":\"Is abuser\"},{\"Value\":true,\"Description\":\"Is tor\"},"
            + "{\"Description\":\"Is VPN\"},\"text\",{\"Value\":-30,\"Description\":\"Stun passed (late arrival, corrected)\"},"
            + "{\"Value\":5}]";
        var row = JsonDocument.Parse(JsonSerializer.Serialize(new { score_details = details })).RootElement;

        var signals = Normalizer.FromHistoryRow(row).Signals;

        Assert.Collection(
            signals,
            s => AssertSignal(s, "proxy", 10, "Is proxy"),
            s => AssertSignal(s, "stun_late_correction", -30, "Stun passed (late arrival, corrected)"),
            s => AssertSignal(s, "unknown", 5, string.Empty));
    }

    private static void AssertSignal(Signal signal, string name, int weight, string? description)
    {
        Assert.Equal(name, signal.Name);
        Assert.Equal(weight, signal.Weight);
        Assert.Equal(description, signal.Description);
    }

    [Fact]
    public void Ip_mismatch_is_derived_for_history_rows_and_cleared_for_search_bots()
    {
        JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

        Assert.True(Normalizer.FromHistoryRow(Row("{\"ip\":\"203.0.113.1\",\"web_rtc_ip\":\"198.51.100.2\"}")).DetectionFlags.IpMismatch);
        Assert.False(Normalizer.FromHistoryRow(Row("{\"ip\":\"203.0.113.1\",\"web_rtc_ip\":\"203.0.113.1\"}")).DetectionFlags.IpMismatch);
        Assert.False(Normalizer.FromHistoryRow(Row("{\"ip\":\"203.0.113.1\",\"web_rtc_ip\":\"0.0.0.0\"}")).DetectionFlags.IpMismatch);
        Assert.False(Normalizer.FromHistoryRow(Row("{\"ip\":\"203.0.113.1\",\"web_rtc_ip\":\"198.51.100.2\",\"is_search_bot\":true}")).DetectionFlags.IpMismatch);
        Assert.True(Normalizer.FromHistoryRow(Row(
            "{\"score_details\":\"[{\\\"Value\\\":0,\\\"Description\\\":\\\"IP ≠ leakIP (a ≠ b)\\\"}]\"}")).DetectionFlags.IpMismatch);
        Assert.True(Normalizer.FromHistoryRow(Row("{\"connection_type\":\"browser_vpn_proxy\"}")).DetectionFlags.BrowserVpnProxy);
    }

    [Fact]
    public void Webhook_data_with_unexpected_shapes_does_not_throw()
    {
        var data = JsonDocument.Parse("""
            {
              "request_id": "02f1d973-84db-4156-a7f7-e799e6bf389b",
              "user_hid": "",
              "public_ip": "not an object",
              "local_ip": { "ip": "0.0.0.0", "country": "Germany" },
              "traffic_source": null,
              "risk_score": 55.9,
              "signals": [ { "name": "vpn", "weight": 15 }, 42, { "name": "proxy" } ],
              "detection_flags": [ true ],
              "observed_at": "not a time",
              "extra_field": { "nested": true }
            }
            """).RootElement;

        var identification = Normalizer.FromWebhookData(data);

        Assert.Null(identification.UserHid);
        Assert.Equal(string.Empty, identification.PublicIp.Ip);
        Assert.Equal(string.Empty, identification.LocalIp.Ip);
        Assert.Equal("Germany", identification.LocalIp.Country);
        Assert.Equal(string.Empty, identification.TrafficSource.Channel);
        Assert.Equal(55, identification.RiskScore);
        Assert.Collection(
            identification.Signals,
            s => Assert.Equal(("vpn", 15), (s.Name, s.Weight)),
            s => Assert.Equal(("proxy", 0), (s.Name, s.Weight)));
        Assert.All(identification.DetectionFlags.ToDictionary().Values, Assert.False);
        Assert.Equal(DateTimeOffset.MinValue, identification.ObservedAt);
        Assert.Equal(IdentificationSource.Webhook, identification.Source);
    }
}
