using System.Text;
using System.Text.Json;
using ShieldLabs.Internal;

namespace ShieldLabs.Tests;

public class WireContractTests
{
    [Fact]
    public void Typed_readers_keep_malformed_and_missing_values_for_existing_coercion()
    {
        var row = JsonUtil.Parse(Encoding.UTF8.GetBytes("{\"score\":\"15\",\"is_vpn\":1,\"connection_type\":\"future_connection\",\"future\":{\"v\":true}}"));
        Assert.Equal(JsonValueKind.String, Wire.Read<long>(row, WireHistoryRow.Score)!.Value.ValueKind);
        Assert.Null(Wire.Read<string>(row, WireHistoryRow.RequestId));
        var result = Normalizer.FromHistoryRow(row);
        // Numeric strings have always fallen back to zero; generation must not coerce them.
        Assert.Equal(0, result.RiskScore);
        Assert.True(result.DetectionFlags.Vpn);
        Assert.Equal("future_connection", result.ConnectionType);
        Assert.True(result.Raw.GetProperty("future").GetProperty("v").GetBoolean());
    }

    [Fact]
    public void Profile_reader_preserves_unknown_raw_and_null_defaults()
    {
        var result = ManagementClient.ParseProfile(Encoding.UTF8.GetBytes("{\"Domain\":null,\"Weight\":\"-3\",\"future\":[1]}"));
        Assert.Equal(string.Empty, result.Domain);
        Assert.Equal(0, result.RemainingIdentifications);
        Assert.Equal(1, result.Raw.GetProperty("future")[0].GetInt32());
    }

    [Fact]
    public void Webhook_reader_accepts_unknown_strings_and_retains_raw_data()
    {
        var result = Normalizer.FromWebhookData(JsonUtil.Parse(Encoding.UTF8.GetBytes("{\"risk_score\":999,\"user_hid\":null,\"traffic_source\":{\"channel\":\"future_channel\"},\"signals\":[{\"name\":\"future_signal\",\"weight\":-30}],\"detection_flags\":{\"vpn\":1},\"future\":true}")));
        Assert.Equal(999, result.RiskScore);
        Assert.Null(result.UserHid);
        Assert.Equal("future_channel", result.TrafficSource.Channel);
        Assert.Equal("future_signal", result.Signals[0].Name);
        Assert.Equal(-30, result.Signals[0].Weight);
        Assert.True(result.DetectionFlags.Vpn);
        Assert.True(result.Raw.GetProperty("future").GetBoolean());
    }

    [Fact]
    public void Generated_parameters_keep_wire_names_and_values()
    {
        var limit = Wire.Parameter<long>(WireSearchHistoryParameters.Limit, 3);
        Assert.Equal("limit", limit.Key);
        Assert.Equal("3", limit.Value);
        Assert.Equal("user_hid", Validation.WireName(LookupType.UserHid));
        Assert.Equal("X-Shield-Domain", Wire.Parameter<string>(WireGetDomainProfileParameters.XShieldDomain, "example.test").Key);
    }
}
