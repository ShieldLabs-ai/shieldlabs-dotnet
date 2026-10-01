using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class IdentificationsTests
{
    private const string RequestId = "a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d";
    private const string TooManyRequests = "{\"error\":\"too many requests\"}\n";
    private const string ServerBusy = "{\"error\":\"server is busy\"}";

    private static string Found => TestClients.Page(new[] { TestClients.Row(RequestId, score: 80) }, 1);

    private static string Empty => Fixtures.Text("history-empty.json");

    private static IEnumerable<double> Millis(IEnumerable<TimeSpan> spans) => spans.Select(d => d.TotalMilliseconds);

    private static Action<HttpResponseMessage> RetryAfter(string value) => r => r.Headers.TryAddWithoutValidation("Retry-After", value);

    private static GetIdentificationOptions WithTimeout(double seconds) => new() { Timeout = TimeSpan.FromSeconds(seconds) };

    /// <summary>A History client on a virtual clock with the given per-attempt timeout.</summary>
    private static ShieldLabsClient Client(FakeHttpHandler handler, FakeTime time, TimeSpan? clientTimeout = null, int maxRetries = 2)
        => new(
            new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, Timeout = clientTimeout ?? TimeSpan.FromSeconds(10), MaxRetries = maxRetries },
            new HttpClient(handler),
            time);

    /// <summary>
    /// One failed lookup of a transient kind: a 5xx status, a 429 without <c>Retry-After</c>, a
    /// refused connection, a reset connection or an attempt that never answers.
    /// </summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Failure(string kind, FakeTime time) => kind switch
    {
        "500" or "502" or "503" or "504" => (_, _) => Task.FromResult(FakeHttpHandler.Response(int.Parse(kind, System.Globalization.CultureInfo.InvariantCulture), ServerBusy)),
        "429" => (_, _) => Task.FromResult(FakeHttpHandler.Response(429, TooManyRequests)),
        "refused" => (_, _) => throw new HttpRequestException("connection refused"),
        "reset" => (_, _) => throw new IOException("reset by peer"),
        "timeout" => (_, _) => time.TimeOutAttempt(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static Type ExceptionFor(string kind) => kind switch
    {
        "429" => typeof(RateLimitException),
        "refused" or "reset" => typeof(ApiConnectionException),
        "timeout" => typeof(ApiTimeoutException),
        _ => typeof(ServerException),
    };

    // Without waiting: one lookup with the client retries.

    [Fact]
    public async Task Without_wait_one_lookup_returns_the_row()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Wait = false });

        Assert.NotNull(identification);
        Assert.Equal(80, identification!.RiskScore);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://account.shieldlabs.ai/api/v1/history/request_id/" + RequestId + "?limit=1&offset=0", request.Uri.AbsoluteUri);
        Assert.Empty(time.Delays);
        Assert.Equal(new[] { 10000.0 }, Millis(time.AttemptTimeouts));
    }

    [Fact]
    public async Task Without_wait_a_missing_row_returns_null()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Wait = false });

        Assert.Null(identification);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Without_wait_the_lookup_uses_the_client_retries()
    {
        var handler = new FakeHttpHandler().Enqueue(502, "<html>bad gateway</html>", "text/html").Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Wait = false });

        Assert.NotNull(identification);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { 500.0 }, Millis(time.Delays));
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData("0", 0)]
    [InlineData("Wed, 01 Jan 2020 00:00:00 GMT", 0)]
    [InlineData("0.5", 500)]
    public async Task Without_wait_a_rate_limit_follows_retry_after_as_sent(string? retryAfter, double expectedMillis)
    {
        // Outside the wait a Retry-After is followed as sent (0 or a past date retries at once), and a
        // 429 without it waits at least 1 s.
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: retryAfter is null ? null : RetryAfter(retryAfter))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Wait = false });

        Assert.NotNull(identification);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { expectedMillis }, Millis(time.Delays));
    }

    // The schedule and the total budget.

    [Fact]
    public async Task Polls_immediately_then_backs_off_until_the_row_appears()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty).Enqueue(200, Empty).Enqueue(200, Empty).Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId.ToUpperInvariant());

        Assert.Equal(RequestId, identification!.RequestId);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500, 1000 }, Millis(time.Delays));
        Assert.All(handler.Requests, r => Assert.Contains("/request_id/" + RequestId, r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Default_schedule_runs_until_the_timeout_then_returns_null()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.Null(identification);
        // Polls at 0, 0.25, 0.75, 1.75, 3.25, 5.25, 7.25, 9.25 and a last one at the 10 s deadline.
        Assert.Equal(new[] { 250.0, 500, 1000, 1500, 2000, 2000, 2000, 750 }, Millis(time.Delays));
        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task Timeout_is_the_total_budget_including_the_time_lookups_take()
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Always((_, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(700));
            return Task.FromResult(FakeHttpHandler.Response(200, Empty));
        });

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(5));

        Assert.Null(identification);
        // Lookups start at 0, 0.95, 2.15, 3.85 and at the 5 s deadline; the last one ends at 5.7 s.
        Assert.Equal(new[] { 250.0, 500, 1000, 450 }, Millis(time.Delays));
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(5.7), time.Elapsed);
    }

    [Fact]
    public async Task Poll_interval_sets_the_first_wait()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromMilliseconds(500), Timeout = TimeSpan.FromSeconds(6) });

        Assert.Equal(new[] { 500.0, 1000, 2000, 2000, 500 }, Millis(time.Delays));
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 1500)]
    [InlineData(4, 2000)]
    [InlineData(50, 2000)]
    public void Default_poll_ladder(int step, double expectedMillis)
    {
        Assert.Equal(expectedMillis, IdentificationsClient.PollWait(step, TimeSpan.FromMilliseconds(250)).TotalMilliseconds);
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 1, 200)]
    [InlineData(100, 2, 400)]
    [InlineData(100, 3, 600)]
    [InlineData(100, 4, 800)]
    [InlineData(100, 5, 800)]
    [InlineData(100, 50, 800)]
    [InlineData(200, 3, 1200)]
    [InlineData(200, 4, 1600)]
    [InlineData(200, 9, 1600)]
    [InlineData(300, 3, 1800)]
    [InlineData(300, 4, 2000)]
    [InlineData(400, 2, 1600)]
    [InlineData(400, 3, 2000)]
    [InlineData(1000, 0, 1000)]
    [InlineData(1000, 1, 2000)]
    [InlineData(1000, 2, 2000)]
    [InlineData(1000, 4, 2000)]
    [InlineData(2000, 0, 2000)]
    [InlineData(2000, 4, 2000)]
    [InlineData(2500, 1, 2500)]
    [InlineData(3000, 0, 3000)]
    [InlineData(3000, 1, 3000)]
    [InlineData(3000, 4, 3000)]
    [InlineData(3000, 50, 3000)]
    [InlineData(20, 0, 20)]
    [InlineData(20, 4, 160)]
    public void Poll_ladder_is_1_2_4_6_then_8_times_the_interval_capped_at_two_seconds_or_the_interval(double intervalMillis, int step, double expectedMillis)
    {
        Assert.Equal(expectedMillis, IdentificationsClient.PollWait(step, TimeSpan.FromMilliseconds(intervalMillis)).TotalMilliseconds);
    }

    [Fact]
    public void Poll_interval_above_two_seconds_is_its_own_cap()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2001), IdentificationsClient.PollWait(0, TimeSpan.FromMilliseconds(2001)));
        Assert.Equal(TimeSpan.FromSeconds(5), IdentificationsClient.PollWait(0, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(5), IdentificationsClient.PollWait(3, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.MaxValue, IdentificationsClient.PollWait(40, TimeSpan.MaxValue));
    }

    [Fact]
    public void Poll_ladder_is_exact_right_below_the_cap()
    {
        // 2 s / 6 is not a whole number of ticks: the largest interval under the cap stays exact.
        var interval = TimeSpan.FromTicks(TimeSpan.FromSeconds(2).Ticks / 6);
        Assert.Equal(TimeSpan.FromTicks(interval.Ticks * 6), IdentificationsClient.PollWait(3, interval));
        Assert.Equal(TimeSpan.FromSeconds(2), IdentificationsClient.PollWait(3, interval + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public async Task Short_poll_interval_levels_off_at_eight_times_the_interval()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(5) });

        Assert.Null(identification);
        // Polls at 0, 0.1, 0.3, 0.7, 1.3, 2.1, 2.9, 3.7, 4.5 and a last one at the 5 s deadline.
        Assert.Equal(new[] { 100.0, 200, 400, 600, 800, 800, 800, 800, 500 }, Millis(time.Delays));
        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), time.Elapsed);
    }

    [Fact]
    public async Task Long_poll_interval_is_used_as_it_is()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromSeconds(5), Timeout = TimeSpan.FromSeconds(7) });

        Assert.Null(identification);
        // Polls at 0, 5 and a last one at the 7 s deadline.
        Assert.Equal(new[] { 5000.0, 2000 }, Millis(time.Delays));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), time.Elapsed);
    }

    /// <summary>Poll interval in milliseconds and the waits of a 10-second wait that never finds the row.</summary>
    public static TheoryData<int, double[]> LadderCases() => new()
    {
        // p, 2p, 4p, 6p, 8p, then 8p, each capped at max(2 s, p); the last wait ends at the deadline.
        { 250, new[] { 250.0, 500, 1000, 1500, 2000, 2000, 2000, 750 } },
        { 1000, new[] { 1000.0, 2000, 2000, 2000, 2000, 1000 } },
        { 3000, new[] { 3000.0, 3000, 3000, 1000 } },
    };

    [Theory]
    [MemberData(nameof(LadderCases))]
    public async Task Waits_follow_the_ladder_for_poll_intervals_of_250_ms_1_s_and_3_s(int intervalMillis, double[] expectedMillis)
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromMilliseconds(intervalMillis) });

        Assert.Null(identification);
        Assert.Equal(expectedMillis, Millis(time.Delays));
        Assert.Equal(expectedMillis.Length + 1, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task Longest_poll_interval_is_cut_short_at_the_deadline()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty).Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.MaxValue, Timeout = TimeSpan.FromDays(3650) });

        Assert.NotNull(identification);
        Assert.Equal(new[] { TimeSpan.FromDays(3650) }, time.Delays);
    }

    [Fact]
    public async Task Very_long_timeout_does_not_overflow()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty).Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.MaxValue });

        Assert.NotNull(identification);
        Assert.Equal(new[] { 250.0 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Zero_timeout_polls_once()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.Zero });

        Assert.Null(identification);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
        Assert.Equal(new[] { 1000.0 }, Millis(time.AttemptTimeouts));
    }

    // Each poll is one HTTP attempt with the timeout min(client timeout, max(time left, 1 s)).

    [Fact]
    public async Task Each_poll_is_one_attempt_whatever_the_client_retries()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(503, ServerBusy)
            .Enqueue(429, TooManyRequests)
            .Enqueue((_, _) => throw new HttpRequestException("connection refused"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await Client(handler, time, maxRetries: 5).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        // Only the polling schedule waits (a 429 stretches its wait to 1 s); no retry backoff inside a poll.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 1000, 1000 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Attempt_timeout_is_the_time_left_but_at_least_one_second()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        await Client(handler, time, clientTimeout: TimeSpan.FromSeconds(10)).Identifications.GetAsync(RequestId);

        // Lookups at 0, 0.25, 0.75, 1.75, 3.25, 5.25, 7.25, 9.25 (0.75 s left) and 10 s (none left).
        Assert.Equal(new[] { 10000.0, 9750, 9250, 8250, 6750, 4750, 2750, 1000, 1000 }, Millis(time.AttemptTimeouts));
    }

    [Fact]
    public async Task Attempt_timeout_never_exceeds_the_client_timeout()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        await Client(handler, time, clientTimeout: TimeSpan.FromSeconds(3)).Identifications.GetAsync(RequestId);

        Assert.Equal(new[] { 3000.0, 3000, 3000, 3000, 3000, 3000, 2750, 1000, 1000 }, Millis(time.AttemptTimeouts));
    }

    [Fact]
    public async Task Client_timeout_below_one_second_caps_every_attempt()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var time = new FakeTime();

        await Client(handler, time, clientTimeout: TimeSpan.FromMilliseconds(400)).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.Equal(new[] { 400.0, 400, 400, 400 }, Millis(time.AttemptTimeouts));
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(1000, 1000)]
    [InlineData(250, 1000)]
    [InlineData(0, 1000)]
    [InlineData(-50, 1000)]
    public void Poll_attempt_timeout_is_the_time_left_but_at_least_one_second(double remainingMillis, double expectedMillis)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMillis), IdentificationsClient.PollAttemptTimeout(TimeSpan.FromMilliseconds(remainingMillis)));
    }

    [Fact]
    public void Attempt_timeout_cap_lowers_but_never_raises_the_client_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ShieldLabs.Internal.HttpPipeline.EffectiveTimeout(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(10), ShieldLabs.Internal.HttpPipeline.EffectiveTimeout(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(3), ShieldLabs.Internal.HttpPipeline.EffectiveTimeout(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(10), ShieldLabs.Internal.HttpPipeline.EffectiveTimeout(TimeSpan.FromSeconds(10), null));
    }

    [Fact]
    public async Task Hanging_api_ends_at_most_one_second_after_the_timeout()
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Always((_, _) => time.TimeOutAttempt());

        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Client(handler, time, clientTimeout: TimeSpan.FromSeconds(3)).Identifications.GetAsync(RequestId));

        // Lookups at 0, 3.25 and 6.75 use the 3 s client timeout; the one at the 10 s deadline gets 1 s.
        Assert.Equal(new[] { 3000.0, 3000, 3000, 1000 }, Millis(time.AttemptTimeouts));
        Assert.Equal(new[] { 250.0, 500, 250 }, Millis(time.Delays));
        Assert.Equal(TimeSpan.FromSeconds(11), time.Elapsed);
    }

    [Fact]
    public async Task Hanging_api_with_the_default_timeouts_ends_at_the_deadline()
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Always((_, _) => time.TimeOutAttempt());

        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        Assert.Contains("within 10 s", error.Message);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task Hanging_api_is_polled_once_per_step_until_the_deadline()
    {
        var handler = new FakeHttpHandler().Always(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var time = new FakeTime();

        // The 50 ms client timeout cancels each attempt on the real clock.
        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Client(handler, time, clientTimeout: TimeSpan.FromMilliseconds(50)).Identifications.GetAsync(RequestId, WithTimeout(1)));

        // Polls at 0, 0.25, 0.75 and at the 1 s deadline; each one is a single attempt.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500, 250 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Wait_timeout_bounds_the_call_against_a_hanging_api()
    {
        var handler = new FakeHttpHandler().Always(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        // Real clock, default per-attempt timeout of 10 s and retries: the wait timeout still wins.
        var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey }, new HttpClient(handler));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<ApiTimeoutException>(
            () => client.Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.FromMilliseconds(300) }));

        stopwatch.Stop();
        Assert.Single(handler.Requests);
        Assert.Contains("within 1 s", error.Message);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Very_long_client_timeout_does_not_break_the_attempt()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Found);
        var client = new ShieldLabsClient(
            new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, Timeout = TimeSpan.FromDays(60) },
            new HttpClient(handler),
            new FakeTime());

        Assert.NotNull(await client.Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.MaxValue }));
    }

    // 429, 5xx, connection errors and timeouts keep the wait going.

    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    [InlineData("429")]
    [InlineData("refused")]
    [InlineData("reset")]
    [InlineData("timeout")]
    public async Task Transient_failure_then_the_row_is_found_on_the_next_poll(string kind)
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Enqueue(Failure(kind, time)).Enqueue(200, Found);

        var identification = await Client(handler, time, clientTimeout: TimeSpan.FromSeconds(1)).Identifications.GetAsync(RequestId);

        Assert.Equal(RequestId, identification!.RequestId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { kind == "429" ? 1000.0 : 250 }, Millis(time.Delays));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("refused")]
    [InlineData("reset")]
    public async Task Transient_failures_until_the_deadline_throw_the_last_one(string kind)
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Always(Failure(kind, time));

        var error = await Assert.ThrowsAnyAsync<ShieldLabsException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1)));

        Assert.IsType(ExceptionFor(kind), error);
        // Polls at 0, 0.25, 0.75 and at the 1 s deadline.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500, 250 }, Millis(time.Delays));
        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed);
    }

    [Fact]
    public async Task Server_errors_on_every_poll_are_thrown_at_the_deadline()
    {
        var handler = new FakeHttpHandler().Always(500, "{\"error\":\"internal error\"}\n");
        var time = new FakeTime();

        await Assert.ThrowsAsync<ServerException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        // One attempt per poll on the normal schedule, no retry backoff inside a poll.
        Assert.Equal(new[] { 250.0, 500, 1000, 1500, 2000, 2000, 2000, 750 }, Millis(time.Delays));
        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task The_error_of_the_last_poll_is_the_one_thrown()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(500, "{\"error\":\"internal error\"}")
            .Enqueue((_, _) => throw new HttpRequestException("connection refused"))
            .Enqueue(502, "<html>bad gateway</html>", "text/html")
            .Enqueue(503, "{\"error\":\"last lookup\"}");
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<ServerException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1)));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("last lookup", error.Error);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task A_connection_error_on_the_last_poll_is_thrown_after_server_errors()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(503, ServerBusy)
            .Enqueue(503, ServerBusy)
            .Enqueue(503, ServerBusy)
            .Enqueue((_, _) => throw new HttpRequestException("connection refused"));
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1)));

        Assert.Contains("connection refused", error.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Null_when_the_last_poll_finds_nothing_after_failed_polls()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(500, "{\"error\":\"internal error\"}")
            .Enqueue((_, _) => throw new HttpRequestException("connection refused"))
            .Enqueue(503, ServerBusy)
            .Enqueue(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.Null(identification);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed);
    }

    [Fact]
    public async Task A_failed_poll_followed_by_empty_polls_returns_null()
    {
        var handler = new FakeHttpHandler().Enqueue(500, "{\"error\":\"internal error\"}\n").Always(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.Null(identification);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Timeouts_keep_polling_and_the_last_one_is_thrown_at_the_deadline()
    {
        var time = new FakeTime();
        var handler = new FakeHttpHandler().Always((_, _) => time.TimeOutAttempt());

        await Assert.ThrowsAsync<ApiTimeoutException>(
            () => Client(handler, time, clientTimeout: TimeSpan.FromSeconds(1)).Identifications.GetAsync(RequestId));

        // Each lookup uses up its 1 s attempt timeout; the last one starts at the 10 s deadline.
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500, 1000, 1500, 1750 }, Millis(time.Delays));
        Assert.Equal(TimeSpan.FromSeconds(11), time.Elapsed);
    }

    // 429 inside the wait.

    [Fact]
    public async Task Rate_limit_without_retry_after_waits_at_least_one_second()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests)
            .Enqueue(200, Empty)
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 1000.0, 500 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Rate_limit_floor_keeps_a_longer_ladder_wait()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests)
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 250.0, 500, 1000, 1500 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Rate_limited_on_every_poll_waits_at_least_one_second_each_time_then_throws()
    {
        var handler = new FakeHttpHandler().Always(429, TooManyRequests);
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        Assert.Null(error.RetryAfter);
        // Polls at 0, 1, 2, 3, 4.5, 6.5, 8.5 and at the 10 s deadline.
        Assert.Equal(new[] { 1000.0, 1000, 1000, 1500, 2000, 2000, 1500 }, Millis(time.Delays));
        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task Rate_limit_floor_is_cut_short_so_the_last_poll_runs_at_the_deadline()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests)
            .Enqueue(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1.5));

        Assert.Null(identification);
        Assert.Equal(new[] { 250.0, 500, 750 }, Millis(time.Delays));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), time.Elapsed);
    }

    [Fact]
    public async Task Rate_limit_inside_the_wait_honours_retry_after()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter("3"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 3000.0 }, Millis(time.Delays));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_after_is_capped_at_ten_seconds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter("60"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(30));

        Assert.NotNull(identification);
        Assert.Equal(new[] { 10000.0 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_shorter_than_the_ladder_wait_keeps_the_ladder()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("1"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 250.0, 500, 1000, 1500, 2000 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_as_an_http_date_is_honoured_inside_the_wait()
    {
        var time = new FakeTime();
        var date = time.UtcNow.AddSeconds(2).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter(date))
            .Enqueue(200, Found);

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 2000.0 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_longer_than_the_time_left_throws_at_once()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter("60"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(4)));

        Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
    }

    [Fact]
    public async Task Retry_after_longer_than_the_time_left_later_in_the_wait_throws_at_once()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("5"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(5)));

        // The 429 arrives at 0.75 s with 4.25 s left.
        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_equal_to_the_time_left_waits_for_the_last_poll()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter("3"))
            .Enqueue(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(3));

        Assert.Null(identification);
        Assert.Equal(new[] { 3000.0 }, Millis(time.Delays));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.25")]
    [InlineData("0.5")]
    public async Task Retry_after_under_one_second_still_waits_one_second(string retryAfter)
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter(retryAfter))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 1000.0 }, Millis(time.Delays));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_after_in_the_past_counts_as_zero_and_waits_one_second()
    {
        var time = new FakeTime();
        var date = time.UtcNow.AddSeconds(-30).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests, configure: RetryAfter(date))
            .Enqueue(200, Found);

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 1000.0 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_zero_keeps_a_longer_ladder_wait()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("0"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId);

        Assert.NotNull(identification);
        Assert.Equal(new[] { 250.0, 500, 1000, 1500 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Retry_after_zero_is_cut_short_so_the_last_poll_runs_at_the_deadline()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("0"))
            .Enqueue(200, Empty);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.Null(identification);
        // The 429 arrives at 0.75 s: its 1 s wait is cut to the 250 ms left, then the last poll runs.
        Assert.Equal(new[] { 250.0, 500, 250 }, Millis(time.Delays));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed);
    }

    [Fact]
    public async Task Retry_after_under_one_second_but_longer_than_the_time_left_throws_at_once()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("0.5"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1)));

        // The 429 arrives at 0.75 s with 0.25 s left.
        Assert.Equal(TimeSpan.FromSeconds(0.5), error.RetryAfter);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { 250.0, 500 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Rate_limited_with_retry_after_zero_on_every_poll_waits_one_second_each_time_then_throws()
    {
        var handler = new FakeHttpHandler().Always(429, TooManyRequests, configure: RetryAfter("0"));
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        Assert.Equal(TimeSpan.Zero, error.RetryAfter);
        // Polls at 0, 1, 2, 3, 4.5, 6.5, 8.5 and at the 10 s deadline.
        Assert.Equal(new[] { 1000.0, 1000, 1000, 1500, 2000, 2000, 1500 }, Millis(time.Delays));
        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), time.Elapsed);
    }

    [Fact]
    public async Task Rate_limit_floor_applies_over_a_short_ladder_wait()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(200, Empty)
            .Enqueue(429, TooManyRequests)
            .Enqueue(200, Empty)
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromMilliseconds(100) });

        Assert.NotNull(identification);
        // The ladder is at 8 x 100 ms when the 429 arrives: that wait lasts 1 s, then the ladder goes on.
        Assert.Equal(new[] { 100.0, 200, 400, 600, 1000, 800 }, Millis(time.Delays));
    }

    [Fact]
    public async Task Rate_limit_keeps_a_ladder_wait_longer_than_two_seconds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, TooManyRequests)
            .Enqueue(429, TooManyRequests, configure: RetryAfter("2"))
            .Enqueue(429, TooManyRequests, configure: RetryAfter("5"))
            .Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(
            RequestId,
            new GetIdentificationOptions { PollInterval = TimeSpan.FromSeconds(3), Timeout = TimeSpan.FromSeconds(20) });

        Assert.NotNull(identification);
        // Every step of a 3 s interval is 3 s: longer than 1 s and than Retry-After: 2; Retry-After: 5 is longer still.
        Assert.Equal(new[] { 3000.0, 3000, 5000 }, Millis(time.Delays));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0.0, 0)]
    [InlineData(-5.0, 0)]
    [InlineData(0.5, 500)]
    [InlineData(3.0, 3000)]
    [InlineData(10.0, 10000)]
    [InlineData(60.0, 10000)]
    public void Requested_pause_is_retry_after_capped_at_ten_seconds(double? retryAfterSeconds, double expectedMillis)
    {
        var retryAfter = retryAfterSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        Assert.Equal(expectedMillis, IdentificationsClient.RequestedPause(retryAfter).TotalMilliseconds);
    }

    [Theory]
    [InlineData(250, 0, 1000)]
    [InlineData(250, 500, 1000)]
    [InlineData(250, 1000, 1000)]
    [InlineData(250, 3000, 3000)]
    [InlineData(250, 10000, 10000)]
    [InlineData(800, 0, 1000)]
    [InlineData(1500, 0, 1500)]
    [InlineData(2000, 1000, 2000)]
    [InlineData(2000, 2500, 2500)]
    [InlineData(3000, 0, 3000)]
    [InlineData(3000, 2000, 3000)]
    [InlineData(3000, 5000, 5000)]
    public void Wait_after_a_rate_limit_is_the_longest_of_the_ladder_wait_one_second_and_retry_after(double ladderMillis, double requestedMillis, double expectedMillis)
    {
        var delay = IdentificationsClient.RateLimitedDelay(TimeSpan.FromMilliseconds(ladderMillis), TimeSpan.FromMilliseconds(requestedMillis));

        Assert.Equal(expectedMillis, delay.TotalMilliseconds);
    }

    // The last poll runs at the deadline.

    [Fact]
    public async Task The_last_poll_at_the_deadline_can_still_find_the_row()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty).Enqueue(200, Empty).Enqueue(200, Empty).Enqueue(200, Found);
        var time = new FakeTime();

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.NotNull(identification);
        Assert.Equal(new[] { 250.0, 500, 250 }, Millis(time.Delays));
        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed);
    }

    [Fact]
    public async Task The_poll_at_the_deadline_stays_the_last_one_when_the_timer_fires_early()
    {
        var handler = new FakeHttpHandler();
        handler.Always((_, _) => handler.Requests.Count > 10
            ? throw new InvalidOperationException("Polled again after the deadline.")
            : Task.FromResult(FakeHttpHandler.Response(200, Empty)));
        var time = new FakeTime { EarlyBy = TimeSpan.FromMilliseconds(5) };

        var identification = await TestClients.History(handler, time).Identifications.GetAsync(RequestId, WithTimeout(1));

        Assert.Null(identification);
        // The last wait is cut to the 260 ms left and returns 5 ms early; no extra poll follows.
        Assert.Equal(new[] { 250.0, 500, 260 }, Millis(time.Delays));
        Assert.Equal(4, handler.Requests.Count);
    }

    // 400, 401, 403 and 404 stop at once.

    [Theory]
    [InlineData(400, typeof(BadRequestException))]
    [InlineData(401, typeof(AuthenticationException))]
    [InlineData(403, typeof(AuthenticationException))]
    [InlineData(404, typeof(NotFoundException))]
    public async Task Client_errors_stop_the_wait_at_once(int status, Type expected)
    {
        var handler = new FakeHttpHandler().Always(status, "{\"error\":\"refused\"}\n", "text/plain; charset=utf-8");
        var time = new FakeTime();

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => Client(handler, time, maxRetries: 5).Identifications.GetAsync(RequestId));

        Assert.IsType(expected, error);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("refused", error.Error);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
    }

    [Theory]
    [InlineData(400, typeof(BadRequestException))]
    [InlineData(401, typeof(AuthenticationException))]
    [InlineData(403, typeof(AuthenticationException))]
    [InlineData(404, typeof(NotFoundException))]
    public async Task Client_error_after_a_transient_failure_stops_at_once(int status, Type expected)
    {
        var handler = new FakeHttpHandler().Enqueue(503, ServerBusy).Always(status, string.Empty, null);
        var time = new FakeTime();

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        Assert.IsType(expected, error);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { 250.0 }, Millis(time.Delays));
    }

    [Theory]
    [InlineData(402, typeof(QuotaExceededException))]
    [InlineData(409, typeof(ApiException))]
    public async Task Other_statuses_that_another_poll_cannot_change_stop_at_once(int status, Type expected)
    {
        var handler = new FakeHttpHandler().Always(status, string.Empty, null);
        var time = new FakeTime();

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => TestClients.History(handler, time).Identifications.GetAsync(RequestId));

        Assert.IsType(expected, error);
        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
    }

    // Validation, cancellation and the real clock.

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Invalid_request_id_throws_before_any_request(string? requestId)
    {
        var handler = new FakeHttpHandler().Always(200, Empty);

        await Assert.ThrowsAsync<ValidationException>(() => TestClients.History(handler).Identifications.GetAsync(requestId!));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_options_throw_before_any_request()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);
        var client = TestClients.History(handler);

        await Assert.ThrowsAsync<ValidationException>(() => client.Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.FromSeconds(-1) }));
        await Assert.ThrowsAsync<ValidationException>(() => client.Identifications.GetAsync(RequestId, new GetIdentificationOptions { PollInterval = TimeSpan.Zero }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_stops_waiting()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler().Always((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(FakeHttpHandler.Response(200, Empty));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestClients.History(handler).Identifications.GetAsync(RequestId, cancellationToken: cts.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_during_a_failed_last_poll_ends_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler().Always((_, _) =>
        {
            cts.Cancel();
            throw new IOException("reset by peer");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TestClients.History(handler).Identifications.GetAsync(RequestId, new GetIdentificationOptions { Timeout = TimeSpan.Zero }, cts.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_real_clock_accepts_a_wait_longer_than_one_timer_allows()
    {
        // A long poll interval with a long budget can ask for a wait of weeks, longer than one timer accepts.
        using var cts = new CancellationTokenSource();
        var wait = SystemTimeSource.Instance.Delay(TimeSpan.FromDays(60), cts.Token);

        Assert.False(wait.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Waits_use_the_real_clock_when_none_is_injected()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Empty).Enqueue(200, Found);
        var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey }, new HttpClient(handler));

        var identification = await client.Identifications.GetAsync(RequestId, new GetIdentificationOptions { PollInterval = TimeSpan.FromMilliseconds(20) });

        Assert.NotNull(identification);
        Assert.Equal(2, handler.Requests.Count);
    }
}
