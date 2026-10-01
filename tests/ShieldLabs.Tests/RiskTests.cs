using System.Text.Json;
using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class RiskTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 36, 0, TimeSpan.Zero);

    public static TheoryData<int, string> BandCases()
    {
        var data = new TheoryData<int, string>();
        foreach (var c in Fixtures.Json("risk-band-cases.json").GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("score").GetInt32(), c.GetProperty("band").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BandCases))]
    public void Band_matches_fixture(int score, string band)
    {
        var expected = band switch
        {
            "trusted" => RiskBand.Trusted,
            "suspicious" => RiskBand.Suspicious,
            "dangerous" => RiskBand.Dangerous,
            "rate_limited" => RiskBand.RateLimited,
            _ => throw new InvalidOperationException(band),
        };
        Assert.Equal(expected, Risk.Band(score));
        Assert.Equal(band == "rate_limited", Risk.IsRateLimited(score));
        Assert.Equal("\"" + band + "\"", JsonSerializer.Serialize(Risk.Band(score)));
    }

    [Theory]
    [InlineData(101, true)]
    [InlineData(100, false)]
    [InlineData(-5, false)]
    public void Rate_limit_marker_is_any_value_above_100(int score, bool expected)
    {
        Assert.Equal(expected, Risk.IsRateLimited(score));
    }

    [Fact]
    public void Missing_identification_is_refused_without_band()
    {
        var evaluation = Risk.Evaluate(null);

        Assert.False(evaluation.Ok);
        Assert.Equal(EvaluationReason.Missing, evaluation.Reason);
        Assert.Null(evaluation.Band);
        Assert.Null(evaluation.Flag);
    }

    [Fact]
    public void Replay_is_checked_before_freshness()
    {
        string? asked = null;
        var evaluation = Risk.Evaluate(
            Make(score: 10, observedAt: Now.AddHours(-1)),
            new EvaluateOptions { Now = Now, IsReplay = id => { asked = id; return true; } });

        Assert.Equal(EvaluationReason.Replayed, evaluation.Reason);
        Assert.Equal(RiskBand.Trusted, evaluation.Band);
        Assert.Equal("7c1e2f4a-3b6d-4e8f-9a0b-1c2d3e4f5a6b", asked);
    }

    [Fact]
    public void Stale_identification_is_refused()
    {
        var evaluation = Risk.Evaluate(Make(score: 10, observedAt: Now.AddMinutes(-5).AddMilliseconds(-1)), new EvaluateOptions { Now = Now });
        Assert.Equal(EvaluationReason.Stale, evaluation.Reason);

        var exactlyMaxAge = Risk.Evaluate(Make(score: 10, observedAt: Now.AddMinutes(-5)), new EvaluateOptions { Now = Now });
        Assert.True(exactlyMaxAge.Ok);
    }

    [Fact]
    public void Unparsable_timestamp_is_always_stale()
    {
        var evaluation = Risk.Evaluate(Make(score: 10, observedAt: DateTimeOffset.MinValue), new EvaluateOptions { Now = Now });
        Assert.Equal(EvaluationReason.Stale, evaluation.Reason);
    }

    [Fact]
    public void Rate_limit_marker_is_refused_before_device_check()
    {
        var identification = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_1a2b3c4d").GetProperty("input"));

        var evaluation = Risk.Evaluate(identification, new EvaluateOptions { Now = identification.ObservedAt });

        Assert.Equal(EvaluationReason.RateLimited, evaluation.Reason);
        Assert.Equal(RiskBand.RateLimited, evaluation.Band);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void Nil_or_empty_device_id_is_refused(string deviceId)
    {
        var evaluation = Risk.Evaluate(Make(score: 10, deviceId: deviceId), new EvaluateOptions { Now = Now });
        Assert.Equal(EvaluationReason.NoDeviceSignals, evaluation.Reason);
    }

    [Fact]
    public void Block_flags_are_checked_in_order_before_bands()
    {
        var identification = Make(score: 85, flags: new DetectionFlags { BrowserAutomation = true, JavascriptDisabled = true });

        var evaluation = Risk.Evaluate(identification, new EvaluateOptions { Now = Now });

        Assert.False(evaluation.Ok);
        Assert.Equal(EvaluationReason.BlockedFlag, evaluation.Reason);
        Assert.Equal("browser_automation", evaluation.Flag);
        Assert.Equal(RiskBand.Dangerous, evaluation.Band);

        var reordered = Risk.Evaluate(identification, new EvaluateOptions
        {
            Now = Now,
            BlockFlags = new[] { DetectionFlagNames.Tor, DetectionFlagNames.JavascriptDisabled, DetectionFlagNames.BrowserAutomation },
        });
        Assert.Equal("javascript_disabled", reordered.Flag);
    }

    [Theory]
    [InlineData("browser-automation")]
    [InlineData("BrowserAutomation")]
    [InlineData("not_a_flag")]
    [InlineData("")]
    public void Unknown_block_flag_names_are_rejected(string flag)
    {
        var options = new EvaluateOptions { Now = Now, BlockFlags = new[] { DetectionFlagNames.Vpn, flag } };

        var error = Assert.Throws<ValidationException>(() => Risk.Evaluate(Make(score: 10), options));
        Assert.Contains("DetectionFlagNames", error.Message);
        // Rejected even when there is no identification, so a typo cannot hide until traffic arrives.
        Assert.Throws<ValidationException>(() => Risk.Evaluate(null, options));
    }

    [Fact]
    public void Null_block_flag_and_undefined_band_are_rejected()
    {
        Assert.Throws<ValidationException>(() => Risk.Evaluate(Make(score: 10), new EvaluateOptions { Now = Now, BlockFlags = new string[] { null! } }));
        var band = Assert.Throws<ValidationException>(() => Risk.Evaluate(Make(score: 10), new EvaluateOptions { Now = Now, BlockBands = new[] { (RiskBand)42 } }));
        Assert.Contains("42", band.Message);
    }

    [Fact]
    public void Every_flag_name_is_accepted_in_block_flags()
    {
        var evaluation = Risk.Evaluate(Make(score: 10), new EvaluateOptions { Now = Now, BlockFlags = DetectionFlagNames.All });

        Assert.True(evaluation.Ok);
    }

    [Fact]
    public void Dangerous_band_is_refused_by_default()
    {
        var identification = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_a5b7c9d1").GetProperty("input"));

        var evaluation = Risk.Evaluate(identification, new EvaluateOptions { Now = identification.ObservedAt.AddSeconds(30) });

        Assert.Equal(EvaluationReason.BlockedBand, evaluation.Reason);
        Assert.Equal(RiskBand.Dangerous, evaluation.Band);
        Assert.Null(evaluation.Flag);
    }

    [Fact]
    public void Trusted_identification_passes()
    {
        var identification = Normalizer.FromHistoryRow(Fixtures.NormalizationCase("history_7c1e2f4a").GetProperty("input"));
        var used = new HashSet<string>();

        var first = Risk.Evaluate(identification, new EvaluateOptions { Now = identification.ObservedAt.AddMinutes(1), IsReplay = id => !used.Add(id) });
        var second = Risk.Evaluate(identification, new EvaluateOptions { Now = identification.ObservedAt.AddMinutes(1), IsReplay = id => !used.Add(id) });

        Assert.True(first.Ok);
        Assert.Null(first.Reason);
        Assert.Equal(RiskBand.Trusted, first.Band);
        Assert.Equal(EvaluationReason.Replayed, second.Reason);
    }

    [Fact]
    public void Options_are_tunable()
    {
        var suspicious = Make(score: 45, flags: new DetectionFlags { Vpn = true });

        Assert.True(Risk.Evaluate(suspicious, new EvaluateOptions { Now = Now }).Ok);
        Assert.Equal(
            EvaluationReason.BlockedBand,
            Risk.Evaluate(suspicious, new EvaluateOptions { Now = Now, BlockBands = new[] { RiskBand.Suspicious, RiskBand.Dangerous } }).Reason);
        Assert.Equal(
            "vpn",
            Risk.Evaluate(suspicious, new EvaluateOptions { Now = Now, BlockFlags = new[] { DetectionFlagNames.Vpn } }).Flag);
        Assert.True(Risk.Evaluate(Make(score: 85), new EvaluateOptions { Now = Now, BlockBands = Array.Empty<RiskBand>() }).Ok);
        Assert.True(Risk.Evaluate(Make(score: 10, observedAt: Now.AddHours(-2)), new EvaluateOptions { Now = Now, MaxAge = TimeSpan.FromHours(3) }).Ok);
        Assert.True(Risk.Evaluate(Make(score: 85), new EvaluateOptions { Now = Now, BlockBands = null!, BlockFlags = null! }).Ok);
    }

    [Fact]
    public void Default_now_is_the_current_time()
    {
        Assert.True(Risk.Evaluate(Make(score: 10, observedAt: DateTimeOffset.UtcNow)).Ok);
    }

    [Fact]
    public void Negative_max_age_is_rejected()
    {
        Assert.Throws<ValidationException>(() => Risk.Evaluate(Make(score: 10), new EvaluateOptions { MaxAge = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void Evaluation_serializes_with_snake_case_values()
    {
        var evaluation = Risk.Evaluate(Make(score: 85, flags: new DetectionFlags { BrowserAutomation = true }), new EvaluateOptions { Now = Now });

        Assert.Equal(
            "{\"ok\":false,\"reason\":\"blocked_flag\",\"band\":\"dangerous\",\"flag\":\"browser_automation\"}",
            JsonSerializer.Serialize(evaluation));
        Assert.Equal("{\"ok\":false,\"reason\":\"missing\",\"band\":null,\"flag\":null}", JsonSerializer.Serialize(Risk.Evaluate(null)));
    }

    private static Identification Make(int score, DateTimeOffset? observedAt = null, string deviceId = "5d9a1f3e-7b2c-5e4d-8f6a-9b0c1d2e3f4a", DetectionFlags? flags = null)
        => new Identification
        {
            RequestId = "7c1e2f4a-3b6d-4e8f-9a0b-1c2d3e4f5a6b",
            DeviceId = deviceId,
            RiskScore = score,
            ObservedAt = observedAt ?? Now.AddSeconds(-10),
            DetectionFlags = flags ?? new DetectionFlags(),
        };
}
