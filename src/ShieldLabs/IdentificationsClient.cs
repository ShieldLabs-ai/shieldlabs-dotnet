using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Options of <see cref="IdentificationsClient.GetAsync"/>.</summary>
public sealed class GetIdentificationOptions
{
    /// <summary>
    /// Keep polling until the identification appears or <see cref="Timeout"/> is spent. Defaults to
    /// true. When false, one lookup is made (retried like any History request, see
    /// <see cref="ShieldLabsClientOptions.MaxRetries"/>) and null is returned if there is no row yet.
    /// </summary>
    public bool Wait { get; set; } = true;

    /// <summary>
    /// The total time budget of the wait. Defaults to 10 seconds: the History row appears about 1 to 3
    /// seconds after the browser call. The last lookup runs at the deadline and still gets up to 1
    /// second to answer (less when the client timeout is shorter), so
    /// <see cref="IdentificationsClient.GetAsync"/> returns at most about 1 second after this time.
    /// Zero makes a single lookup.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The base of the waits between polls: they are 1, 2, 4, 6 and then 8 times this interval, each
    /// capped at 2 seconds, or at this interval when it is longer (250 ms, 500 ms, 1 s, 1.5 s, then
    /// 2 s with the default; 1 s waits 1, 2, 2, 2 s; 3 s polls every 3 s), to stay inside the History
    /// API rate limit, which all callers of a domain share. Defaults to 250 milliseconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Reads one identification by its request ID. Obtain it from <see cref="ShieldLabsClient.Identifications"/>.
/// </summary>
public sealed class IdentificationsClient
{
    /// <summary>
    /// Longest wait of the polling schedule for a poll interval up to 2 s. A longer poll interval is
    /// its own cap: each wait is at most max(2 s, poll interval).
    /// </summary>
    internal static readonly TimeSpan MaxPollWait = TimeSpan.FromSeconds(2);

    /// <summary>Shortest timeout of one lookup, even when less time is left before the deadline.</summary>
    internal static readonly TimeSpan MinPollAttempt = TimeSpan.FromSeconds(1);

    /// <summary>From this step on every wait is 8 times the poll interval, so the step counter stops here.</summary>
    private const int LastLadderStep = 4;

    private readonly HistoryClient _history;
    private readonly ITimeSource _time;

    internal IdentificationsClient(HistoryClient history, ITimeSource time)
    {
        _history = history;
        _time = time;
    }

    /// <summary>
    /// Returns the identification for a request ID, waiting for the verdict by default. Scoring is
    /// asynchronous: the History row appears about 1 to 3 seconds after the browser call and can be
    /// refined for up to about 10 seconds while follow-up checks finish; this returns the first
    /// version it sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While waiting, <see cref="GetIdentificationOptions.Timeout"/> is the total time budget of the
    /// call. The first lookup is immediate, then the waits grow: 1, 2, 4, 6 and then 8 times
    /// <see cref="GetIdentificationOptions.PollInterval"/>, each capped at 2 s, or at the interval
    /// when it is longer (with the default interval: 250 ms, 500 ms, 1 s, 1.5 s, then every 2 s; an
    /// interval of 3 s polls every 3 s). A wait that would pass the deadline is cut short, so the
    /// last lookup runs at the deadline.
    /// </para>
    /// <para>
    /// Each lookup is exactly one HTTP attempt (the client retries do not apply) with the timeout
    /// min(client timeout, max(time left, 1 s)), so the call returns at most about 1 second after
    /// the deadline.
    /// </para>
    /// <para>
    /// A 429, a 5xx, a timeout or a connection error does not end the wait: the next lookup follows
    /// the schedule. After a 429 the next wait is at least 1 second: the longest of the scheduled
    /// wait, 1 second and <c>Retry-After</c> capped at 10 seconds (<c>Retry-After: 0</c> or a date
    /// in the past counts as 0), cut short at the deadline like any other wait. When that capped
    /// <c>Retry-After</c> is longer than the time left, that <see cref="RateLimitException"/> is
    /// thrown at once. When time is up, the exception of the last lookup is thrown if it failed;
    /// otherwise the result is null.
    /// </para>
    /// <para>
    /// A 400, 401, 403 or 404 stops the wait at once with its exception, because a wrong key or base
    /// URL does not heal; so does any other status that another lookup cannot change.
    /// </para>
    /// </remarks>
    /// <param name="requestId">The request ID the browser received (a UUID).</param>
    /// <param name="options">Waiting options; by default the wait has a total budget of 10 seconds.</param>
    /// <param name="cancellationToken">Cancels the lookup and any wait.</param>
    /// <returns>
    /// The identification, or null when there is no row (when time is up, or immediately when
    /// <see cref="GetIdentificationOptions.Wait"/> is false). Null means "unverified", never "clean".
    /// </returns>
    /// <exception cref="ValidationException">The request ID is not a UUID or an option is invalid; nothing was sent.</exception>
    /// <exception cref="BadRequestException">HTTP 400; the wait stops at once.</exception>
    /// <exception cref="AuthenticationException">HTTP 401 or 403, the key was rejected; the wait stops at once.</exception>
    /// <exception cref="NotFoundException">HTTP 404, usually a wrong base URL; the wait stops at once.</exception>
    /// <exception cref="RateLimitException">The last lookup was rate limited when time was up, or <c>Retry-After</c>, capped at 10 seconds, asked for more time than was left.</exception>
    /// <exception cref="ServerException">The last lookup failed with a 5xx when time was up.</exception>
    /// <exception cref="ApiConnectionException">The last lookup could not reach the server when time was up.</exception>
    /// <exception cref="ApiTimeoutException">The last lookup did not complete in time when time was up.</exception>
    /// <exception cref="ApiException">Another HTTP error status; the wait stops at once.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Identification?> GetAsync(
        string requestId,
        GetIdentificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var value = Validation.LookupSegment(LookupType.RequestId, requestId, nameof(requestId));
        var wait = options?.Wait ?? true;
        var timeout = options?.Timeout ?? TimeSpan.FromSeconds(10);
        var pollInterval = options?.PollInterval ?? TimeSpan.FromMilliseconds(250);
        if (timeout < TimeSpan.Zero)
        {
            throw new ValidationException("Timeout must be zero or greater.");
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ValidationException("PollInterval must be greater than zero.");
        }

        const string wireType = "request_id";
        if (!wait)
        {
            var page = await _history.SearchCoreAsync(wireType, value, 1, 0, retryRateLimited: true, cancellationToken).ConfigureAwait(false);
            return page.Data.Count > 0 ? page.Data[0] : null;
        }

        return await PollAsync(wireType, value, timeout, pollInterval, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Identification?> PollAsync(
        string wireType,
        string value,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var start = _time.Elapsed;
        var deadline = timeout > TimeSpan.MaxValue - start ? TimeSpan.MaxValue : start + timeout;
        var step = 0;
        var lastPoll = false;
        while (true)
        {
            ShieldLabsException? failure = null;
            try
            {
                // Exactly one HTTP attempt per lookup: the client retries do not apply here, the
                // schedule below does the retrying, and the attempt gets the time left (at least 1 s).
                var page = await _history.SearchCoreAsync(
                    wireType,
                    value,
                    1,
                    0,
                    retryRateLimited: false,
                    cancellationToken,
                    maxRetries: 0,
                    attemptTimeout: PollAttemptTimeout(deadline - _time.Elapsed)).ConfigureAwait(false);
                if (page.Data.Count > 0)
                {
                    return page.Data[0];
                }
            }
            catch (ShieldLabsException ex) when (IsTransientPollFailure(ex))
            {
                // Any other error (400, 401, 403, 404 and statuses another lookup cannot change)
                // leaves the loop at once. A cancelled call ends as cancelled, not with this error.
                cancellationToken.ThrowIfCancellationRequested();
                failure = ex;
            }

            var remaining = deadline - _time.Elapsed;
            if (lastPoll || remaining <= TimeSpan.Zero)
            {
                // Time is up: the outcome of the last lookup stands.
                if (failure is not null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                return null;
            }

            var delay = PollWait(step, pollInterval);
            if (step < LastLadderStep)
            {
                step++;
            }

            if (failure is RateLimitException rateLimited)
            {
                var requested = RequestedPause(rateLimited.RetryAfter);
                if (requested > remaining)
                {
                    // The server asks for a pause that ends after the deadline: no later lookup fits.
                    ExceptionDispatchInfo.Capture(rateLimited).Throw();
                }

                delay = RateLimitedDelay(delay, requested);
            }

            if (delay >= remaining)
            {
                // Cut the wait short so that the last lookup runs at the deadline. It stays the last
                // one even when the timer fires a little early.
                delay = remaining;
                lastPoll = true;
            }

            await _time.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Failures that mean "try again later" while waiting: 429, 5xx, connection errors and timeouts.</summary>
    internal static bool IsTransientPollFailure(ShieldLabsException failure)
        => failure is RateLimitException || failure is ServerException || failure is ApiConnectionException || failure is ApiTimeoutException;

    /// <summary>
    /// Timeout of one lookup before the client timeout applies: the time left before the deadline,
    /// but at least <see cref="MinPollAttempt"/>. The pipeline lowers it to the client timeout when
    /// that is shorter, so a lookup gets min(client timeout, max(time left, 1 s)).
    /// </summary>
    internal static TimeSpan PollAttemptTimeout(TimeSpan remaining) => remaining > MinPollAttempt ? remaining : MinPollAttempt;

    /// <summary>
    /// Wait before poll number <paramref name="step"/> + 2: 1, 2, 4, 6 and then 8 times the poll
    /// interval, each capped at max(2 s, poll interval), so an interval above 2 s is used as it is.
    /// With the default: 250 ms, 500 ms, 1 s, 1.5 s, 2 s, 2 s, ...
    /// </summary>
    internal static TimeSpan PollWait(int step, TimeSpan pollInterval)
    {
        if (pollInterval >= MaxPollWait)
        {
            // The interval is its own cap, and no multiple of it is shorter.
            return pollInterval;
        }

        long factor = step switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 6,
            _ => 8,
        };

        // Compare before multiplying, so very long intervals saturate at the cap instead of overflowing.
        return pollInterval.Ticks > MaxPollWait.Ticks / factor
            ? MaxPollWait
            : TimeSpan.FromTicks(pollInterval.Ticks * factor);
    }

    /// <summary>
    /// The pause a 429 asks for: <c>Retry-After</c> capped at 10 seconds. No <c>Retry-After</c>,
    /// <c>Retry-After: 0</c> and a date in the past all count as zero.
    /// </summary>
    internal static TimeSpan RequestedPause(TimeSpan? retryAfter)
    {
        if (retryAfter is not TimeSpan requested || requested <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return requested < HttpPipeline.MaxRetryAfter ? requested : HttpPipeline.MaxRetryAfter;
    }

    /// <summary>
    /// The wait after a 429, with or without <c>Retry-After</c>: the longest of the scheduled wait
    /// <paramref name="ladderWait"/>, 1 second (<see cref="HttpPipeline.RateLimitedWait"/>) and the
    /// <paramref name="requested"/> pause (see <see cref="RequestedPause"/>).
    /// </summary>
    internal static TimeSpan RateLimitedDelay(TimeSpan ladderWait, TimeSpan requested)
    {
        var minimum = HttpPipeline.RateLimitedWait;
        var delay = ladderWait > minimum ? ladderWait : minimum;
        return requested > delay ? requested : delay;
    }
}
