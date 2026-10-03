using System.Net;
using System.Security.Cryptography;
using System.Text;
using ShieldLabs;

using var http = new HttpClient(new FixtureHandler());
var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = "sec_11111111-22222222-33333333", MaxRetries = 0 }, http);
var page = await client.History.SearchAsync(LookupType.UserHid, "anonymous", new HistorySearchOptions { Limit = 3, Offset = 2 });
if (page.Total != 1 || page.Data[0].RiskScore != 15 || !page.Data[0].DetectionFlags.Vpn
    || page.Data[0].ConnectionType != "future_connection" || !page.Data[0].Raw.GetProperty("future").GetBoolean())
    throw new Exception("History contract failed");
var management = new ManagementClient(new ManagementClientOptions { SecretKey = "fixture-only", Domain = "example.test", MaxRetries = 0 }, http);
var profile = await management.GetProfileAsync();
if (profile.Domain != "example.test" || profile.RemainingIdentifications != -3 || !profile.Raw.GetProperty("future").GetBoolean())
    throw new Exception("Profile contract failed");
const string payload = "{\"event_type\":\"identification.scored\",\"schema_version\":\"2026-06-01\",\"data\":{\"risk_score\":999,\"user_hid\":null,\"signals\":[{\"name\":\"new_signal\",\"weight\":-30}],\"future\":true}}";
const string secret = "whsec_fixture_only";
var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
var scored = (IdentificationScoredEvent)WebhookEvents.ConstructEvent(payload, signature, secret);
if (scored.Data.RiskScore != 999 || scored.Data.Signals[0].Weight != -30 || scored.Data.UserHid != null
    || !scored.Data.Raw.GetProperty("future").GetBoolean()) throw new Exception("Webhook contract failed");
Console.WriteLine("Packed consumer: History, profile, signed webhook, tolerant fields and raw data passed.");

sealed class FixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body;
        if (request.RequestUri!.AbsolutePath == "/v1/profile")
        {
            if (request.RequestUri.PathAndQuery != "/v1/profile") throw new Exception("Unexpected profile query parameter");
            if (request.Headers.GetValues("X-Shield-Domain").Single() != "example.test") throw new Exception("Missing profile header");
            body = "{\"Domain\":\"example.test\",\"Weight\":-3,\"future\":true}";
        }
        else
        {
            if (request.RequestUri.PathAndQuery != "/api/v1/history/user_hid/anonymous?limit=3&offset=2") throw new Exception("History parameters changed");
            body = "{\"total\":1,\"data\":[{\"score\":15,\"is_vpn\":1,\"connection_type\":\"future_connection\",\"future\":true}]}";
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
