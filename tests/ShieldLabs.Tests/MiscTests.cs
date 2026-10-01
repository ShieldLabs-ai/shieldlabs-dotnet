using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class UserHidTests
{
    [Theory]
    [InlineData("user-42", "server-secret", "7bafe016d701998889aeaa7ff7cff26a38240d2963072e28a0c58b52c4cd7e71")]
    [InlineData("josé@example.com", "sécret", "671a2e715b0ff9ca5b4d4f44c6a62a2ee7eba1083726dc501f19e984fee01869")]
    public void Is_hmac_sha256_as_lowercase_hex(string userId, string secret, string expected)
    {
        var hid = UserHid.FromUserId(userId, secret);

        Assert.Equal(expected, hid);
        Assert.Equal(64, hid.Length);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData(null, "secret")]
    [InlineData("user-42", "")]
    [InlineData("user-42", null)]
    public void Rejects_empty_input(string? userId, string? secret)
    {
        Assert.Throws<ValidationException>(() => UserHid.FromUserId(userId!, secret!));
    }
}

public class SdkInfoTests
{
    [Fact]
    public void Version_comes_from_the_assembly_without_build_metadata()
    {
        var assembly = typeof(ShieldLabsClient).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0], SdkInfo.Version);
        Assert.Equal(assembly.GetName().Version!.ToString(3), SdkInfo.Version.Split('-')[0]);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+", SdkInfo.Version);
    }

    [Fact]
    public void Version_falls_back_to_the_assembly_version()
    {
        // A dynamic assembly carries no informational version attribute.
        var dynamic = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("VersionProbe") { Version = new Version(2, 3, 4, 5) },
            System.Reflection.Emit.AssemblyBuilderAccess.Run);

        Assert.Equal("2.3.4", SdkInfo.ReadVersion(dynamic));
    }

    [Fact]
    public void User_agent_names_the_sdk_and_runtime()
    {
        Assert.StartsWith("shieldlabs-dotnet/" + SdkInfo.Version + " (", SdkInfo.UserAgent);
        Assert.EndsWith(")", SdkInfo.UserAgent);
        Assert.Contains(".NET", SdkInfo.UserAgent);
    }

    [Fact]
    public void Library_build_under_test_is_reported()
    {
        // Shows which build of the library ran (net8.0 by default, netstandard2.0 in the extra CI run).
        var framework = typeof(ShieldLabsClient).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName;
        var expected = Environment.GetEnvironmentVariable("SHIELDLABS_TEST_LIBRARY_FRAMEWORK");
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(expected, framework);
        }

        Assert.False(string.IsNullOrEmpty(framework));
    }

    [Fact]
    public void Default_http_client_is_shared_and_lazy()
    {
        Assert.Same(DefaultHttp.Instance, DefaultHttp.Instance);
        Assert.Equal(Timeout.InfiniteTimeSpan, DefaultHttp.Instance.Timeout);
    }
}

public class ModelTests
{
    [Fact]
    public void Identification_serializes_with_webhook_field_names()
    {
        var identification = Normalizer.FromWebhookData(Fixtures.NormalizationCase("webhook_scored").GetProperty("input"));

        var json = JsonNode.Parse(JsonSerializer.Serialize(identification))!.AsObject();

        var expectedKeys = Fixtures.NormalizationCase("webhook_scored").GetProperty("expected").EnumerateObject().Select(p => p.Name).Append("raw");
        Assert.Equal(expectedKeys.OrderBy(k => k, StringComparer.Ordinal), json.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("webhook", json["source"]!.GetValue<string>());
        Assert.Equal(DetectionFlagNames.All, json["detection_flags"]!.AsObject().Select(p => p.Key));
        Assert.Equal("proxy", json["signals"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Netherlands", json["public_ip"]!["country"]!.GetValue<string>());
        Assert.Equal("gclid", json["traffic_source"]!["click_id_type"]!.GetValue<string>());
        // UTC with milliseconds and Z, the webhook wire form (never "+00:00").
        Assert.Equal("2026-09-30T12:34:57.482Z", json["observed_at"]!.GetValue<string>());
    }

    [Fact]
    public void Timestamps_serialize_as_utc_milliseconds_with_z()
    {
        var history = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_a5b7c9d1").GetProperty("input"));
        Assert.Contains("\"observed_at\":\"2026-09-30T12:34:56.123Z\"", JsonSerializer.Serialize(history));

        var profile = ManagementClient.ParseProfile(Fixtures.Bytes("management-profile.json"));
        var expected = Fixtures.Json("management-profile-expected.json").GetProperty("created_at").GetString();
        Assert.Equal(expected, JsonNode.Parse(JsonSerializer.Serialize(profile))!["created_at"]!.GetValue<string>());
        Assert.Contains("\"created_at\":null", JsonSerializer.Serialize(new DomainProfile()));

        var ping = new WebhookPingEvent { CreatedAt = new DateTimeOffset(2026, 9, 30, 14, 34, 56, 789, TimeSpan.FromHours(2)) };
        Assert.Contains("\"created_at\":\"2026-09-30T12:34:56.789Z\"", JsonSerializer.Serialize(ping));
        Assert.Contains("\"observed_at\":\"0001-01-01T00:00:00.000Z\"", JsonSerializer.Serialize(new Identification()));
    }

    [Theory]
    [InlineData("\"2026-09-30T12:34:56.789Z\"")]
    [InlineData("\"2026-09-30T12:34:56.789+00:00\"")]
    [InlineData("\"2026-09-30T14:34:56.789+02:00\"")]
    [InlineData("\"2026-09-30T12:34:56.789999Z\"")]
    public void Timestamps_deserialize_from_any_offset(string json)
    {
        var copy = JsonSerializer.Deserialize<Identification>("{\"observed_at\":" + json + "}")!;

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.Zero), copy.ObservedAt);
        Assert.Equal(TimeSpan.Zero, copy.ObservedAt.Offset);
    }

    [Fact]
    public void Timestamp_converters_reject_other_tokens_and_keep_null()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Identification>("{\"observed_at\":1790771696123}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Identification>("{\"observed_at\":\"yesterday\"}"));
        Assert.Null(JsonSerializer.Deserialize<DomainProfile>("{\"created_at\":null}")!.CreatedAt);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero),
            JsonSerializer.Deserialize<DomainProfile>("{\"created_at\":\"2026-01-15T09:00:00.000Z\"}")!.CreatedAt);
    }

    [Fact]
    public void Identification_round_trips_through_json()
    {
        var original = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_9e8d7c6b").GetProperty("input"));

        var copy = JsonSerializer.Deserialize<Identification>(JsonSerializer.Serialize(original))!;

        Fixtures.AssertJsonEqual(Fixtures.Canonical(original), Fixtures.Canonical(copy), "round trip");
        Assert.Equal(IdentificationSource.History, copy.Source);
        Fixtures.AssertJsonEqual(JsonNode.Parse(original.Raw.GetRawText()), JsonNode.Parse(copy.Raw.GetRawText()), "raw");
    }

    [Fact]
    public void Default_models_serialize()
    {
        var json = JsonSerializer.Serialize(new Identification());

        Assert.Contains("\"raw\":{}", json);
        Assert.Contains("\"source\":\"webhook\"", json);
        Assert.Contains("\"user_hid\":null", json);
        Assert.Contains("\"raw\":{}", JsonSerializer.Serialize(new DomainProfile()));
        Assert.Contains("\"data\":[]", JsonSerializer.Serialize(new HistoryPage()));
    }

    [Fact]
    public void Enum_converter_rejects_unknown_values()
    {
        Assert.Equal(IdentificationSource.History, JsonSerializer.Deserialize<IdentificationSource>("\"history\""));
        Assert.Equal(RiskBand.RateLimited, JsonSerializer.Deserialize<RiskBand>("\"RATE_LIMITED\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RiskBand>("\"severe\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RiskBand>("3"));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((RiskBand)42));
        Assert.Equal("no_device_signals", SnakeCaseEnumConverter<EvaluationReason>.ToSnakeCase(nameof(EvaluationReason.NoDeviceSignals)));
    }

    [Fact]
    public void Detection_flags_lookup_by_wire_name()
    {
        var flags = new DetectionFlags { Tor = true, CheckIncomplete = true };

        Assert.True(flags.IsSet("tor"));
        Assert.True(flags.IsSet(DetectionFlagNames.CheckIncomplete));
        Assert.False(flags.IsSet("vpn"));
        Assert.False(flags.IsSet("not_a_flag"));
        Assert.Equal(19, DetectionFlagNames.All.Count);
        Assert.Equal(DetectionFlagNames.All, flags.ToDictionary().Keys);
        Assert.Equal(2, flags.ToDictionary().Values.Count(v => v));
    }

    [Fact]
    public void Every_flag_name_maps_to_its_own_property()
    {
        foreach (var name in DetectionFlagNames.All)
        {
            var property = typeof(DetectionFlags).GetProperties()
                .Single(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name == name);
            var flags = new DetectionFlags();
            property.SetValue(flags, true);

            Assert.True(flags.IsSet(name), name);
            Assert.Equal(1, flags.ToDictionary().Values.Count(v => v));
        }
    }

    [Fact]
    public void Webhook_event_types_serialize_their_envelope_fields()
    {
        var json = JsonSerializer.Serialize(new WebhookPingEvent { EventType = WebhookEventTypes.Ping, SchemaVersion = WebhookEvents.SchemaVersion });

        Assert.Contains("\"event_type\":\"webhook.ping\"", json);
        Assert.Contains("\"schema_version\":\"2026-06-01\"", json);
        Assert.Contains("\"data\"", JsonSerializer.Serialize(new IdentificationScoredEvent()));
        Assert.Contains("\"event_type\":\"\"", JsonSerializer.Serialize(new UnknownWebhookEvent()));
    }
}

public class HexTests
{
    [Theory]
    [InlineData("00ff10", true)]
    [InlineData("00FF10", true)]
    [InlineData("0", false)]
    [InlineData("zz", false)]
    [InlineData("0g", false)]
    [InlineData("", true)]
    public void Decodes_hex(string text, bool ok)
    {
        Assert.Equal(ok, Hex.TryDecode(text, out _));
    }

    [Fact]
    public void Fixed_time_equals_compares_content_and_length()
    {
        Assert.True(Hex.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
        Assert.False(Hex.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1, 3 }));
        Assert.False(Hex.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1 }));
        Assert.Equal("00ff10", Hex.ToLower(new byte[] { 0x00, 0xFF, 0x10 }));
    }
}
