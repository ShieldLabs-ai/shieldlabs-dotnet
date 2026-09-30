using System.Text;
using System.Text.Json;
using ShieldLabs.Internal;

namespace ShieldLabs.Tests;

public class JsonUtilTests
{
    private static JsonElement? Value(string json) => JsonDocument.Parse("{\"v\":" + json + "}").RootElement.GetProperty("v");

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData("-3", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"false\"", true)]
    [InlineData("[]", false)]
    [InlineData("[0]", true)]
    [InlineData("{}", false)]
    [InlineData("{\"a\":1}", true)]
    public void Truthiness(string json, bool expected)
    {
        Assert.Equal(expected, JsonUtil.Truthy(Value(json)));
    }

    [Fact]
    public void Missing_values_are_falsy_and_empty()
    {
        Assert.False(JsonUtil.Truthy(null));
        Assert.Equal(string.Empty, JsonUtil.AsString(null));
        Assert.Null(JsonUtil.StringOrNull(null));
        Assert.Equal(7, JsonUtil.AsInt(null, 7));
        Assert.Equal(7, JsonUtil.AsLong(null, 7));
        Assert.Null(JsonUtil.Get(default, "x"));
    }

    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("12", "12")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "")]
    [InlineData("[1]", "")]
    [InlineData("{\"a\":1}", "")]
    public void Converts_values_to_strings(string json, string expected)
    {
        Assert.Equal(expected, JsonUtil.AsString(Value(json)));
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-7.9", -7)]
    [InlineData("1e2", 100)]
    [InlineData("99999999999", 0)]
    [InlineData("\"5\"", 0)]
    public void Reads_integers(string json, int expected)
    {
        Assert.Equal(expected, JsonUtil.AsInt(Value(json)));
    }

    [Theory]
    [InlineData("99999999999", 99999999999)]
    [InlineData("-12.5", -12)]
    [InlineData("1e30", 0)]
    [InlineData("\"5\"", 0)]
    public void Reads_longs(string json, long expected)
    {
        Assert.Equal(expected, JsonUtil.AsLong(Value(json)));
    }

    [Fact]
    public void Exact_integers_reject_fractions()
    {
        Assert.True(JsonUtil.TryGetExactInt(Value("-30"), out var negative));
        Assert.Equal(-30, negative);
        Assert.False(JsonUtil.TryGetExactInt(Value("10.0"), out _));
        Assert.False(JsonUtil.TryGetExactInt(Value("true"), out _));
    }

    [Theory]
    [InlineData("{\"a\":1} x")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{/*c*/\"a\":1}")]
    [InlineData("")]
    public void Parse_rejects_invalid_documents(string text)
    {
        Assert.ThrowsAny<JsonException>(() => JsonUtil.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Parse_skips_a_byte_order_mark_and_trailing_whitespace()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}  \n")).ToArray();
        Assert.Equal(1, JsonUtil.Parse(bytes).GetProperty("a").GetInt32());
        Assert.Equal("123", JsonUtil.FormatInvariant(123));
    }

    [Fact]
    public void Score_details_with_a_byte_order_mark_are_ignored()
    {
        var row = JsonDocument.Parse(JsonSerializer.Serialize(new { score_details = "﻿[{\"Value\":10,\"Description\":\"Is proxy\"}]" })).RootElement;
        Assert.Empty(Normalizer.FromHistoryRow(row).Signals);
    }
}
