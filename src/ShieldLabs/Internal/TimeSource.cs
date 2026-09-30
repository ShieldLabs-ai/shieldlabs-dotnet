using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShieldLabs.Internal;

/// <summary>Clock, delay and jitter source. Replaced by a virtual clock in tests.</summary>
internal interface ITimeSource
{
    /// <summary>Monotonic time since an arbitrary origin.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Wall clock time, used to interpret HTTP-date headers.</summary>
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels <paramref name="source"/> after <paramref name="delay"/>: the timeout of one HTTP
    /// attempt. <see cref="Timeout.InfiniteTimeSpan"/> never cancels.
    /// </summary>
    void CancelAfter(CancellationTokenSource source, TimeSpan delay);

    /// <summary>A random number in [0, 1).</summary>
    double NextJitter();
}

internal sealed class SystemTimeSource : ITimeSource
{
    internal static readonly SystemTimeSource Instance = new SystemTimeSource();

    /// <summary>
    /// Longest delay one <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts on every target:
    /// <see cref="int.MaxValue"/> milliseconds, about 24.8 days.
    /// </summary>
    internal static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private static readonly long Origin = Stopwatch.GetTimestamp();
    private readonly object _lock = new object();
    private readonly Random _random = new Random();

    private SystemTimeSource()
    {
    }

    public TimeSpan Elapsed
        => TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - Origin) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return delay <= MaxTimerDelay ? Task.Delay(delay, cancellationToken) : LongDelay(delay, cancellationToken);
    }

    public void CancelAfter(CancellationTokenSource source, TimeSpan delay)
    {
        // CancelAfter accepts at most int.MaxValue milliseconds (about 24.8 days); longer is unbounded.
        if (delay != Timeout.InfiniteTimeSpan && delay.TotalMilliseconds <= int.MaxValue)
        {
            source.CancelAfter(delay);
        }
    }

    public double NextJitter()
    {
        lock (_lock)
        {
            return _random.NextDouble();
        }
    }

    /// <summary>
    /// A wait longer than <see cref="MaxTimerDelay"/>, possible with a long poll interval and a long
    /// budget, runs in steps of at most that length instead of failing.
    /// </summary>
    private static async Task LongDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        while (delay > TimeSpan.Zero)
        {
            var step = delay < MaxTimerDelay ? delay : MaxTimerDelay;
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            delay -= step;
        }
    }
}
