using System.Text;
using System.Text.Json.Nodes;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class WebhookEventTests
{
    private const string Secret = "whsec_00112233445566778899aabbccddeeff";

    private static string Header(byte[] body, string secret = Secret) => "sha256=" + WebhookSignatureTests.Sign(secret, body);

    [Fact]
    public void Scored_event_from_exact_bytes()
    {
        var body = Fixtures.Bytes("webhook-identification-scored.raw.txt");
        var vector = Fixtures.SignatureVector("valid_scored_with_escaped_ampersand");
        Assert.Equal(Convert.FromBase64String(vector.GetProperty("body_base64").GetString()!), body);

        var evt = WebhookEvents.ConstructEvent(body, vector.GetProperty("signature_header").GetString(), Secret);

        var scored = Assert.IsType<IdentificationScoredEvent>(evt);
        Assert.Equal(WebhookEventTypes.IdentificationScored, scored.EventType);
        Assert.Equal("2026-06-01", scored.SchemaVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 57, 482, TimeSpan.Zero), scored.CreatedAt);
        Assert.Equal("https://shop.example.com/signup?utm_source=google&utm_medium=cpc&gclid=abc123", scored.Data.TrafficSource.LandingUrl);
        var expected = Fixtures.NormalizationCase("webhook_scored").GetProperty("expected").GetRawText();
        Fixtures.AssertJsonEqual(JsonNode.Parse(expected), Fixtures.Canonical(scored.Data), "scored");
        Assert.Equal("identification.scored", scored.Raw.GetProperty("event_type").GetString());
    }

    [Fact]
    public void Scored_event_from_pretty_fixture_and_string_payload()
    {
        var text = Fixtures.Text("webhook-identification-scored.json");

        var evt = WebhookEvents.ConstructEvent(text, Header(Encoding.UTF8.GetBytes(text)), "whsec_old", Secret);

        var scored = Assert.IsType<IdentificationScoredEvent>(evt);
        Assert.Equal(80, scored.Data.RiskScore);
        Assert.Equal(RiskBand.Dangerous, Risk.Band(scored.Data.RiskScore));
        Assert.True(scored.Data.DetectionFlags.AntiDetectBrowser);
        Assert.Equal("Netherlands", scored.Data.PublicIp.Country);
    }

    [Fact]
    public void Ping_event_from_exact_bytes()
    {
        var body = Fixtures.Bytes("webhook-ping.raw.txt");

        var evt = WebhookEvents.ConstructEvent(body, "sha256=ea2685733d254f7028fb031c4214583b0650de01e6c8c93131236024edd9fdd8", Secret);

        var ping = Assert.IsType<WebhookPingEvent>(evt);
        Assert.Equal(WebhookEventTypes.Ping, ping.EventType);
        Assert.Equal(WebhookEvents.SchemaVersion, ping.SchemaVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.Zero), ping.CreatedAt);
    }

    [Fact]
    public void Ping_event_from_pretty_fixture()
    {
        var body = Fixtures.Bytes("webhook-ping.json");
        Assert.IsType<WebhookPingEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));
    }

    [Fact]
    public void Rate_limited_event_carries_the_999_marker()
    {
        var body = Fixtures.Bytes("webhook-rate-limited.json");

        var scored = Assert.IsType<IdentificationScoredEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));

        Assert.Equal(999, scored.Data.RiskScore);
        Assert.True(Risk.IsRateLimited(scored.Data.RiskScore));
        Assert.Equal(RiskBand.RateLimited, Risk.Band(scored.Data.RiskScore));
        var signal = Assert.Single(scored.Data.Signals);
        Assert.Equal(SignalNames.RateLimited, signal.Name);
        Assert.Equal(999, signal.Weight);
        var expected = Fixtures.NormalizationCase("webhook_rate_limited").GetProperty("expected").GetRawText();
        Fixtures.AssertJsonEqual(JsonNode.Parse(expected), Fixtures.Canonical(scored.Data), "rate limited");
    }

    [Fact]
    public void Test_delivery_with_17_flags_parses_with_missing_flags_false()
    {
        var body = Fixtures.Bytes("webhook-test-delivery.json");
        var flagsOnWire = JsonNode.Parse(body)!["data"]!["detection_flags"]!.AsObject();
        Assert.Equal(17, flagsOnWire.Count);

        var scored = Assert.IsType<IdentificationScoredEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));

        Assert.False(scored.Data.DetectionFlags.BrowserAutomation);
        Assert.False(scored.Data.DetectionFlags.SearchBot);
        Assert.True(scored.Data.DetectionFlags.Abuser);
        Assert.Null(scored.Data.UserHid);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.Zero), scored.CreatedAt);
        var expected = Fixtures.NormalizationCase("webhook_test_delivery").GetProperty("expected").GetRawText();
        Fixtures.AssertJsonEqual(JsonNode.Parse(expected), Fixtures.Canonical(scored.Data), "test delivery");
    }

    [Fact]
    public void Unknown_event_type_and_schema_version_are_accepted()
    {
        var body = Encoding.UTF8.GetBytes("{\"event_type\":\"identification.refined\",\"schema_version\":\"2027-01-01\",\"created_at\":\"2026-09-30T12:00:00Z\",\"data\":{}}");

        var evt = WebhookEvents.ConstructEvent(body, Header(body), Secret);

        var unknown = Assert.IsType<UnknownWebhookEvent>(evt);
        Assert.Equal("identification.refined", unknown.EventType);
        Assert.Equal("2027-01-01", unknown.SchemaVersion);
    }

    [Fact]
    public void Scored_event_with_future_schema_version_still_parses()
    {
        var body = Encoding.UTF8.GetBytes("{\"event_type\":\"identification.scored\",\"schema_version\":\"2030-01-01\",\"data\":{\"request_id\":\"02f1d973-84db-4156-a7f7-e799e6bf389b\",\"new_field\":1}}");

        var scored = Assert.IsType<IdentificationScoredEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));

        Assert.Equal("02f1d973-84db-4156-a7f7-e799e6bf389b", scored.Data.RequestId);
        Assert.Equal(DateTimeOffset.MinValue, scored.CreatedAt);
        Assert.Equal(1, scored.Data.Raw.GetProperty("new_field").GetInt32());
    }

    [Fact]
    public void Envelope_without_event_type_is_an_unknown_event()
    {
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var unknown = Assert.IsType<UnknownWebhookEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));
        Assert.Equal(string.Empty, unknown.EventType);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"event_type\":\"identification.scored\"}")]
    [InlineData("{\"event_type\":\"identification.scored\",\"data\":[]}")]
    [InlineData("{\"event_type\":\"webhook.ping\"} trailing")]
    [InlineData("")]
    public void Verified_but_malformed_bodies_throw_parse_errors(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        Assert.Throws<WebhookParseException>(() => WebhookEvents.ConstructEvent(body, Header(body), Secret));
    }

    [Fact]
    public void Invalid_utf8_is_a_parse_error()
    {
        var body = new byte[] { (byte)'{', (byte)'"', 0xFF, 0xFE, (byte)'"', (byte)':', (byte)'1', (byte)'}' };
        Assert.Throws<WebhookParseException>(() => WebhookEvents.ConstructEvent(body, Header(body), Secret));
    }

    [Fact]
    public void Byte_order_mark_is_tolerated()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Fixtures.Bytes("webhook-ping.raw.txt")).ToArray();
        Assert.IsType<WebhookPingEvent>(WebhookEvents.ConstructEvent(body, Header(body), Secret));
    }

    [Fact]
    public void Signature_failures_throw_with_a_reason()
    {
        var body = Fixtures.Bytes("webhook-ping.raw.txt");
        var good = Header(body);

        Assert.Contains("missing", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, null, Secret)).Message);
        Assert.Contains("sha256=", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, "md5=abc", Secret)).Message);
        Assert.Contains("secret", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, good)).Message);
        Assert.Contains("secret", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, good, string.Empty)).Message);
        Assert.Contains("does not match", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, good, "whsec_other")).Message);
        Assert.Contains("payload", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent((byte[])null!, good, Secret)).Message);
        Assert.Contains("payload", Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent((string)null!, good, Secret)).Message);
    }

    [Fact]
    public void Rotation_accepts_any_matching_secret()
    {
        var body = Fixtures.Bytes("webhook-ping.raw.txt");
        var header = Header(body, "whsec_new_secret_value");

        Assert.IsType<WebhookPingEvent>(WebhookEvents.ConstructEvent(body, header, Secret, "whsec_new_secret_value"));
        Assert.IsType<WebhookPingEvent>(WebhookEvents.ConstructEvent(body, header, "whsec_new_secret_value", Secret));
    }

    [Fact]
    public void Events_can_be_matched_with_pattern_matching()
    {
        var body = Fixtures.Bytes("webhook-rate-limited.json");
        WebhookEvent evt = WebhookEvents.ConstructEvent(body, Header(body), Secret);

        var described = evt switch
        {
            IdentificationScoredEvent scored => "scored " + scored.Data.RequestId,
            WebhookPingEvent => "ping",
            _ => "unknown",
        };

        Assert.Equal("scored 1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", described);
    }
}
