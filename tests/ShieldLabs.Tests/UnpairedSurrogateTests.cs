using System.Text;
using System.Text.Json;
using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

/// <summary>
/// JSON strings with an escaped unpaired surrogate are valid JSON that JsonElement.GetString
/// rejects. Parsing must still succeed and keep the documented exception contract.
/// </summary>
public class UnpairedSurrogateTests
{
    private const string Secret = "whsec_00112233445566778899aabbccddeeff";

    // JSON text of a string whose value is an escaped lone high surrogate: "\ud800".
    private const string LoneSurrogate = "\"\\ud800\"";

    [Fact]
    public void Json_readers_fall_back_to_the_escaped_text()
    {
        var value = JsonDocument.Parse("{\"v\":" + LoneSurrogate + "}").RootElement.GetProperty("v");

        Assert.Equal("\\ud800", JsonUtil.AsString(value));
        Assert.Equal("\\ud800", JsonUtil.StringOrNull(value));
        Assert.True(JsonUtil.Truthy(value));
        Assert.Equal("\\ud800", JsonUtil.StringOrEmpty(value));
    }

    [Fact]
    public void Valid_surrogate_pairs_are_decoded_as_usual()
    {
        var value = JsonDocument.Parse("{\"v\":\"\\ud83d\\ude00\"}").RootElement.GetProperty("v");

        Assert.Equal("\U0001F600", JsonUtil.AsString(value));
    }

    [Fact]
    public void Verified_webhook_with_unpaired_surrogates_parses()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"event_type\":\"identification.scored\",\"schema_version\":\"2026-06-01\",\"created_at\":\"2026-09-30T12:00:00Z\","
            + "\"data\":{\"request_id\":" + LoneSurrogate + ",\"user_hid\":\"a\\udc00b\",\"connection_type\":" + LoneSurrogate + ","
            + "\"public_ip\":{\"ip\":\"203.0.113.24\",\"country\":" + LoneSurrogate + "},"
            + "\"signals\":[{\"name\":\"\\ud800x\",\"weight\":10}],\"detection_flags\":{\"vpn\":true}}}");
        var header = "sha256=" + WebhookSignatureTests.Sign(Secret, body);

        var scored = Assert.IsType<IdentificationScoredEvent>(WebhookEvents.ConstructEvent(body, header, Secret));

        Assert.Equal("\\ud800", scored.Data.RequestId);
        Assert.Equal("a\\udc00b", scored.Data.UserHid);
        Assert.Equal("\\ud800", scored.Data.PublicIp.Country);
        Assert.Equal("\\ud800x", Assert.Single(scored.Data.Signals).Name);
        Assert.True(scored.Data.DetectionFlags.Vpn);
    }

    [Fact]
    public void Webhook_event_type_with_an_unpaired_surrogate_is_an_unknown_event()
    {
        var body = Encoding.UTF8.GetBytes("{\"event_type\":" + LoneSurrogate + ",\"schema_version\":" + LoneSurrogate + "}");
        var header = "sha256=" + WebhookSignatureTests.Sign(Secret, body);

        var unknown = Assert.IsType<UnknownWebhookEvent>(WebhookEvents.ConstructEvent(body, header, Secret));

        Assert.Equal("\\ud800", unknown.EventType);
    }

    [Fact]
    public async Task History_row_with_unpaired_surrogates_parses()
    {
        var row = "{\"request_id\":\"02f1d973-84db-4156-a7f7-e799e6bf389b\",\"user_hid\":" + LoneSurrogate
            + ",\"connection_type\":" + LoneSurrogate + ",\"webrtc_leak_source\":" + LoneSurrogate
            + ",\"score\":10,\"score_details\":\"[{\\\"Value\\\":10,\\\"Description\\\":\\\"\\\\ud800 check\\\"}]\""
            + ",\"created_at\":\"2026-09-30 13:20:30.250\"}";
        var handler = new FakeHttpHandler().Enqueue(200, TestClients.Page(new[] { row }, 1));

        var page = await TestClients.History(handler).History.SearchAsync(LookupType.RequestId, "02f1d973-84db-4156-a7f7-e799e6bf389b");

        var identification = Assert.Single(page.Data);
        Assert.Equal("\\ud800", identification.UserHid);
        Assert.Equal("\\ud800", identification.ConnectionType);
        var signal = Assert.Single(identification.Signals);
        Assert.Equal(10, signal.Weight);
        Assert.Equal("\\ud800 check", signal.Description);
    }

    [Theory]
    [InlineData("{\"error\":" + LoneSurrogate + "}")]
    [InlineData(LoneSurrogate)]
    public async Task Error_bodies_with_unpaired_surrogates_still_build_the_exception(string body)
    {
        var handler = new FakeHttpHandler().Always(400, body, "text/plain; charset=utf-8");

        var error = await Assert.ThrowsAsync<BadRequestException>(() => TestClients.History(handler).History.SearchAsync(LookupType.UserHid, "anonymous"));

        Assert.Equal("\\ud800", error.Error);
        Assert.Equal(body, error.Body);
    }

    [Fact]
    public async Task Management_profile_with_unpaired_surrogates_parses()
    {
        var handler = new FakeHttpHandler().Enqueue(200, "{\"Domain\":" + LoneSurrogate + ",\"Weight\":5,\"CreatedAt\":" + LoneSurrogate + "}");

        var profile = await TestClients.Management(handler).GetProfileAsync();

        Assert.Equal("\\ud800", profile.Domain);
        Assert.Equal(5, profile.RemainingIdentifications);
        Assert.Null(profile.CreatedAt);
    }
}
