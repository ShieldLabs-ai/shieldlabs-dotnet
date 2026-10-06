using System.Text.Json;
using ShieldLabs.Internal;

namespace ShieldLabs.Tests;

public sealed class ClientIdentityTests
{
    [Fact]
    public void HistoryAndWebhookKeepSavedSubjectAndRevision()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "client-identity.json"));
        using var document = JsonDocument.Parse("{\"risk_score\":70,\"score\":70,\"client_identity\":" + fixture + "}");
        foreach (var value in new[] { Normalizer.FromHistoryRow(document.RootElement), Normalizer.FromWebhookData(document.RootElement) })
        {
            Assert.Equal(70, value.RiskScore);
            var identity = Assert.IsType<ClientIdentity>(value.ClientIdentity);
            Assert.Equal(1, identity.ClassificationRevision);
            Assert.Equal("openai", identity.Claims[0].ProviderId);
            Assert.Equal("GPTBot", identity.Claims[0].AgentName);
            Assert.Equal("provider", identity.Verified[0].Subject);
            Assert.Equal("openai", identity.Verified[0].ValueId);
            Assert.Equal("published_ip", identity.Evidence[0].Method);
            Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(fixture).RootElement), JsonSerializer.Serialize(identity));
        }
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"client_identity\":null}")]
    [InlineData("{\"client_identity\":{}}")]
    [InlineData("{\"client_identity\":\"bad\"}")]
    public void AbsentOrMalformedIsOptional(string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = Normalizer.FromWebhookData(document.RootElement);
        Assert.Null(value.ClientIdentity);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.False(serialized.RootElement.TryGetProperty("client_identity", out _));
    }
    [Fact]
    public void FutureStatesAndUnknownAttributesSurvive()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "client-identity.json"));
        fixture = fixture.Replace("\"available\"", "\"future_state\"");
        using var document = JsonDocument.Parse("{\"client_identity\":" + fixture.TrimEnd()[..^1] + ",\"future\":true}}");
        var identity = Assert.IsType<ClientIdentity>(Normalizer.FromWebhookData(document.RootElement).ClientIdentity);
        Assert.Equal("future_state", identity.Availability);
        Assert.True(identity.Raw.GetProperty("future").GetBoolean());
    }
}
