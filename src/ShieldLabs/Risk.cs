using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Risk band of a Risk Score. Bands are computed client-side; none is sent on the wire.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<RiskBand>))]
public enum RiskBand
{
    /// <summary>Risk Score 0 to 29 (<c>"trusted"</c>).</summary>
    Trusted,

    /// <summary>Risk Score 30 to 59 (<c>"suspicious"</c>).</summary>
    Suspicious,

    /// <summary>Risk Score 60 to 100 (<c>"dangerous"</c>).</summary>
    Dangerous,

    /// <summary>A value above 100 (999): the rate-limit marker, not a score (<c>"rate_limited"</c>).</summary>
    RateLimited,
}

/// <summary>Why <see cref="Risk.Evaluate"/> refused an identification.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<EvaluationReason>))]
public enum EvaluationReason
{
    /// <summary>No identification was found: treat the action as unverified (<c>"missing"</c>).</summary>
    Missing,

    /// <summary>The request ID was already used for another action (<c>"replayed"</c>).</summary>
    Replayed,

    /// <summary>The identification is older than <see cref="EvaluateOptions.MaxAge"/> (<c>"stale"</c>).</summary>
    Stale,

    /// <summary>The Risk Score is the rate-limit marker (above 100) (<c>"rate_limited"</c>).</summary>
    RateLimited,

    /// <summary>The device ID is the nil UUID: no usable device signals (<c>"no_device_signals"</c>).</summary>
    NoDeviceSignals,

    /// <summary>A flag listed in <see cref="EvaluateOptions.BlockFlags"/> is set (<c>"blocked_flag"</c>).</summary>
    BlockedFlag,

    /// <summary>The risk band is listed in <see cref="EvaluateOptions.BlockBands"/> (<c>"blocked_band"</c>).</summary>
    BlockedBand,
}

/// <summary>
/// Options of <see cref="Risk.Evaluate"/>. The defaults are a starting point: tune them to your
/// product and traffic.
/// </summary>
public sealed class EvaluateOptions
{
    /// <summary>Oldest acceptable identification, by <see cref="Identification.ObservedAt"/>. Defaults to 5 minutes.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The current time. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public DateTimeOffset? Now { get; set; }

    /// <summary>Bands that refuse the action. Defaults to <see cref="RiskBand.Dangerous"/>.</summary>
    public IReadOnlyCollection<RiskBand> BlockBands { get; set; } = new[] { RiskBand.Dangerous };

    /// <summary>
    /// Detection flags (wire names, see <see cref="DetectionFlagNames"/>) that refuse the action,
    /// checked in order. Defaults to <c>browser_automation</c> and <c>javascript_disabled</c>. A name
    /// that is not one of the 19 flags throws <see cref="ValidationException"/>, so a typo never
    /// switches a block off.
    /// </summary>
    public IReadOnlyList<string> BlockFlags { get; set; } = new[] { DetectionFlagNames.BrowserAutomation, DetectionFlagNames.JavascriptDisabled };

    /// <summary>
    /// Optional replay check: return true when the request ID was already used. The SDK never stores
    /// request IDs itself. The check must answer synchronously: with an asynchronous store (Redis,
    /// a database), claim the request ID first with an atomic insert-if-absent and pass the result,
    /// for example <c>IsReplay = _ =&gt; !claimed</c>.
    /// </summary>
    public Func<string, bool>? IsReplay { get; set; }
}

/// <summary>The outcome of <see cref="Risk.Evaluate"/>.</summary>
public sealed class Evaluation
{
    /// <summary>True when the action may proceed.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Why the action was refused, or null when <see cref="Ok"/> is true.</summary>
    [JsonPropertyName("reason")]
    public EvaluationReason? Reason { get; init; }

    /// <summary>The risk band of the identification, or null when it was missing.</summary>
    [JsonPropertyName("band")]
    public RiskBand? Band { get; init; }

    /// <summary>The flag that refused the action when <see cref="Reason"/> is <see cref="EvaluationReason.BlockedFlag"/>.</summary>
    [JsonPropertyName("flag")]
    public string? Flag { get; init; }
}

/// <summary>Risk helpers: bands, the rate-limit marker and a reusable guard policy.</summary>
public static class Risk
{
    /// <summary>
    /// Risk band of a score: <see cref="RiskBand.Trusted"/> 0 to 29, <see cref="RiskBand.Suspicious"/>
    /// 30 to 59, <see cref="RiskBand.Dangerous"/> 60 to 100, and <see cref="RiskBand.RateLimited"/>
    /// for a value above 100 (the 999 rate-limit marker).
    /// </summary>
    public static RiskBand Band(int riskScore)
    {
        if (riskScore > 100)
        {
            return RiskBand.RateLimited;
        }

        if (riskScore >= 60)
        {
            return RiskBand.Dangerous;
        }

        return riskScore >= 30 ? RiskBand.Suspicious : RiskBand.Trusted;
    }

    /// <summary>True when the value is the rate-limit marker (above 100), not a Risk Score.</summary>
    public static bool IsRateLimited(int riskScore) => riskScore > 100;

    /// <summary>
    /// Applies a guard policy to an identification, checking in this order: missing, replayed
    /// request ID (<see cref="EvaluateOptions.IsReplay"/>), older than
    /// <see cref="EvaluateOptions.MaxAge"/>, rate-limit marker, nil or empty device ID, any of
    /// <see cref="EvaluateOptions.BlockFlags"/>, then a band in <see cref="EvaluateOptions.BlockBands"/>.
    /// </summary>
    /// <param name="identification">The identification, or null when none was found.</param>
    /// <param name="options">Policy options; null uses the defaults.</param>
    /// <exception cref="ValidationException">
    /// <see cref="EvaluateOptions.MaxAge"/> is negative, <see cref="EvaluateOptions.BlockFlags"/>
    /// holds a name that is not a detection flag, or <see cref="EvaluateOptions.BlockBands"/> holds
    /// an undefined band.
    /// </exception>
    public static Evaluation Evaluate(Identification? identification, EvaluateOptions? options = null)
    {
        options ??= new EvaluateOptions();
        if (options.MaxAge < TimeSpan.Zero)
        {
            throw new ValidationException("MaxAge must be zero or greater.");
        }

        ValidateBlockLists(options);

        if (identification is null)
        {
            return new Evaluation { Ok = false, Reason = EvaluationReason.Missing };
        }

        var band = Band(identification.RiskScore);
        if (options.IsReplay is not null && options.IsReplay(identification.RequestId))
        {
            return Refuse(EvaluationReason.Replayed, band);
        }

        var now = options.Now ?? DateTimeOffset.UtcNow;
        if (now - identification.ObservedAt > options.MaxAge)
        {
            return Refuse(EvaluationReason.Stale, band);
        }

        if (IsRateLimited(identification.RiskScore))
        {
            return Refuse(EvaluationReason.RateLimited, band);
        }

        if (string.IsNullOrEmpty(identification.DeviceId) || identification.DeviceId == Normalizer.NilUuid)
        {
            return Refuse(EvaluationReason.NoDeviceSignals, band);
        }

        if (options.BlockFlags is not null)
        {
            foreach (var flag in options.BlockFlags)
            {
                if (identification.DetectionFlags.IsSet(flag))
                {
                    return new Evaluation { Ok = false, Reason = EvaluationReason.BlockedFlag, Band = band, Flag = flag };
                }
            }
        }

        if (options.BlockBands is not null)
        {
            foreach (var blocked in options.BlockBands)
            {
                if (blocked == band)
                {
                    return Refuse(EvaluationReason.BlockedBand, band);
                }
            }
        }

        return new Evaluation { Ok = true, Band = band };
    }

    private static Evaluation Refuse(EvaluationReason reason, RiskBand band)
        => new Evaluation { Ok = false, Reason = reason, Band = band };

    private static void ValidateBlockLists(EvaluateOptions options)
    {
        if (options.BlockFlags is not null)
        {
            foreach (var flag in options.BlockFlags)
            {
                if (flag is null || !IsFlagName(flag))
                {
                    throw new ValidationException(
                        $"BlockFlags contains {(flag is null ? "null" : "\"" + flag + "\"")}, which is not a detection flag. Use the names in DetectionFlagNames, for example browser_automation.");
                }
            }
        }

        if (options.BlockBands is not null)
        {
            foreach (var band in options.BlockBands)
            {
                if (band < RiskBand.Trusted || band > RiskBand.RateLimited)
                {
                    throw new ValidationException($"BlockBands contains the undefined band {(int)band}. Use the RiskBand values.");
                }
            }
        }
    }

    private static bool IsFlagName(string name)
    {
        foreach (var known in DetectionFlagNames.All)
        {
            if (string.Equals(known, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
