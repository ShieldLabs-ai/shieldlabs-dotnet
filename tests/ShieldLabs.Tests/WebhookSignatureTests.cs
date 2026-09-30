using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class WebhookSignatureTests
{
    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var v in Fixtures.Json("webhook-signature-vectors.json").GetProperty("vectors").EnumerateArray())
        {
            data.Add(v.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Fact]
    public void Fixture_has_21_vectors()
    {
        Assert.Equal(21, VectorNames().Count);
        Assert.Equal(WebhookSignature.HeaderName, Fixtures.Json("webhook-signature-vectors.json").GetProperty("header_name").GetString());
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Vector_verifies_as_expected(string name)
    {
        var vector = Fixtures.SignatureVector(name);
        var body = Convert.FromBase64String(vector.GetProperty("body_base64").GetString()!);
        var header = vector.GetProperty("signature_header").GetString();
        var secrets = Secrets(vector);
        var valid = vector.GetProperty("valid").GetBoolean();

        Assert.Equal(valid, WebhookSignature.Verify(body, header, secrets));
        Assert.Equal(valid, WebhookSignature.Verify(vector.GetProperty("body").GetString()!, header, secrets));

        if (valid)
        {
            Assert.NotNull(WebhookEvents.ConstructEvent(body, header, secrets));
        }
        else
        {
            Assert.Throws<SignatureVerificationException>(() => WebhookEvents.ConstructEvent(body, header, secrets));
        }
    }

    [Fact]
    public void Invalid_inputs_return_false_and_never_throw()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var header = "sha256=" + Sign("whsec_a", body);

        Assert.True(WebhookSignature.Verify(body, header, "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, null, "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, "   ", "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, header));
        Assert.False(WebhookSignature.Verify(body, header, (string[])null!));
        Assert.False(WebhookSignature.Verify(body, header, string.Empty, null!));
        Assert.False(WebhookSignature.Verify((byte[])null!, header, "whsec_a"));
        Assert.False(WebhookSignature.Verify((string)null!, header, "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, "SHA256=" + Sign("whsec_a", body), "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, header + "00", "whsec_a"));
        Assert.False(WebhookSignature.Verify(body, "sha256= " + Sign("whsec_a", body), "whsec_a"));
    }

    [Fact]
    public void Empty_secrets_in_a_list_are_ignored()
    {
        var body = Encoding.UTF8.GetBytes("{\"event_type\":\"webhook.ping\"}");
        var header = "sha256=" + Sign("whsec_new", body);

        Assert.True(WebhookSignature.Verify(body, header, string.Empty, "whsec_new"));
    }

    [Fact]
    public void Secret_is_used_as_is_including_prefix_and_whitespace()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var header = "sha256=" + Sign("whsec_abc", body);

        Assert.True(WebhookSignature.Verify(body, header, "whsec_abc"));
        Assert.False(WebhookSignature.Verify(body, header, "abc"));
        Assert.False(WebhookSignature.Verify(body, header, "whsec_abc "));
    }

    private static string[] Secrets(JsonElement vector)
        => vector.TryGetProperty("secrets", out var list)
            ? list.EnumerateArray().Select(s => s.GetString()!).ToArray()
            : new[] { vector.GetProperty("secret").GetString()! };

    internal static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }
}
