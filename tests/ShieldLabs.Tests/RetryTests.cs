using System.Net;
using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class RetryTests
{
    private const string UserHid = "anonymous";

    private static string Empty => Fixtures.Text("history-empty.json");

    [Fact]
    public async Task Server_error_then_success_is_retried_with_backoff()
    {
        var handler = new FakeHttpHandler().Enqueue(502, "<html>bad gateway</html>", "text/html").Enqueue(200, Empty);
        var time = new FakeTime();

        var page = await TestClients.History(handler, time).History.SearchAsync(LookupType.UserHid, UserHid);

        Assert.Empty(page.Data);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(0.5) }, time.Delays);
    }

    [Fact]
    public async Task Max_retries_zero_sends_one_request()
    {
        var handler = new FakeHttpHandler().Always(500, "{\"error\":\"internal error\"}\n");

        await Assert.ThrowsAsync<ServerException>(() => TestClients.History(handler, maxRetries: 0).History.SearchAsync(LookupType.UserHid, UserHid));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task History_rate_limit_is_retried_and_retry_after_is_capped_at_10_seconds()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "30"))
            .Enqueue(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "1.5"))
            .Enqueue(200, Empty);
        var time = new FakeTime();

        await TestClients.History(handler, time).History.SearchAsync(LookupType.UserHid, UserHid);

        Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1.5) }, time.Delays);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.5", 500)]
    [InlineData("3", 3000)]
    [InlineData("Wed, 01 Jan 2020 00:00:00 GMT", 0)]
    public async Task History_rate_limit_follows_retry_after_as_sent(string retryAfter, double expectedMillis)
    {
        // Outside the wait for a verdict there is no 1 s minimum: Retry-After: 0 or a past date retries at once.
        var handler = new FakeHttpHandler()
            .Enqueue(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", retryAfter))
            .Enqueue(200, Empty);
        var time = new FakeTime();

        await TestClients.History(handler, time).History.SearchAsync(LookupType.UserHid, UserHid);

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(expectedMillis) }, time.Delays);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task History_rate_limit_without_retry_after_waits_at_least_1_second()
    {
        // The backoff (0.25 s, 0.5 s and 1 s at the lowest jitter) is raised to 1 s after a 429 without
        // a usable Retry-After; a longer backoff (2 s) is kept.
        var handler = new FakeHttpHandler()
            .Enqueue(429, "{\"error\":\"too many requests\"}\n")
            .Enqueue(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "soon"))
            .Enqueue(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "-1"))
            .Enqueue(429, "{\"error\":\"too many requests\"}\n")
            .Enqueue(200, Empty);
        var time = new FakeTime { Jitter = 0.0 };

        await TestClients.History(handler, time, maxRetries: 4).History.SearchAsync(LookupType.UserHid, UserHid);

        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, time.Delays);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0.0, 1, 1.0)]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(1.0, 2, 1.0)]
    [InlineData(0.0, 3, 1.0)]
    [InlineData(1.0, 3, 2.0)]
    [InlineData(1.0, 9, 8.0)]
    public void Rate_limited_backoff_is_at_least_1_second(double jitter, int retryNumber, double expectedSeconds)
    {
        var pipeline = new HttpPipeline(new HttpClient(), ApiSurface.History, "k", Array.Empty<KeyValuePair<string, string>>(), TimeSpan.FromSeconds(1), 2, new FakeTime { Jitter = jitter });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), pipeline.RetryDelay(retryNumber, null, rateLimited: true));
        Assert.Equal(TimeSpan.Zero, pipeline.RetryDelay(retryNumber, TimeSpan.Zero, rateLimited: true));
        Assert.Equal(TimeSpan.FromMilliseconds(300), pipeline.RetryDelay(retryNumber, TimeSpan.FromMilliseconds(300), rateLimited: true));
    }

    [Fact]
    public async Task Retry_after_as_http_date_is_understood()
    {
        var time = new FakeTime();
        var date = time.UtcNow.AddSeconds(4).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        var handler = new FakeHttpHandler()
            .Enqueue(503, "{\"error\":\"server is busy\"}", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", date))
            .Enqueue(503, "{\"error\":\"server is busy\"}", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "Wed, 01 Jan 2020 00:00:00 GMT"))
            .Enqueue(503, "{\"error\":\"server is busy\"}", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "soon"))
            .Enqueue(200, Empty);

        await TestClients.History(handler, time, maxRetries: 3).History.SearchAsync(LookupType.UserHid, UserHid);

        Assert.Equal(new[] { TimeSpan.FromSeconds(4), TimeSpan.Zero, TimeSpan.FromSeconds(2) }, time.Delays);
    }

    [Fact]
    public async Task Rate_limit_exception_reports_retry_after()
    {
        var handler = new FakeHttpHandler().Always(429, "{\"error\":\"too many requests\"}\n", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "2"));

        var error = await Assert.ThrowsAsync<RateLimitException>(() => TestClients.History(handler, maxRetries: 0).History.SearchAsync(LookupType.UserHid, UserHid));

        Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
        Assert.Equal("too many requests", error.Error);
    }

    [Fact]
    public async Task Management_rate_limit_is_never_retried()
    {
        var handler = new FakeHttpHandler().Always(429, "{\"error\":\"too many requests\"}", configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "1"));
        var time = new FakeTime();

        await Assert.ThrowsAsync<RateLimitException>(() => TestClients.Management(handler, time, maxRetries: 5).GetProfileAsync());

        Assert.Single(handler.Requests);
        Assert.Empty(time.Delays);
    }

    [Theory]
    [InlineData(0.0, 1, 0.25)]
    [InlineData(1.0, 1, 0.5)]
    [InlineData(1.0, 2, 1.0)]
    [InlineData(1.0, 3, 2.0)]
    [InlineData(1.0, 5, 8.0)]
    [InlineData(1.0, 9, 8.0)]
    [InlineData(0.0, 9, 4.0)]
    public void Backoff_is_exponential_with_jitter_and_capped(double jitter, int retryNumber, double expectedSeconds)
    {
        var pipeline = new HttpPipeline(new HttpClient(), ApiSurface.History, "k", Array.Empty<KeyValuePair<string, string>>(), TimeSpan.FromSeconds(1), 2, new FakeTime { Jitter = jitter });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), pipeline.RetryDelay(retryNumber, null, rateLimited: false));
    }

    [Fact]
    public void Real_jitter_stays_within_bounds()
    {
        var pipeline = new HttpPipeline(new HttpClient(), ApiSurface.History, "k", Array.Empty<KeyValuePair<string, string>>(), TimeSpan.FromSeconds(1), 2, SystemTimeSource.Instance);

        for (var i = 0; i < 50; i++)
        {
            var delay = pipeline.RetryDelay(1, null, rateLimited: false);
            Assert.InRange(delay, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.5));
        }
    }

    [Fact]
    public async Task Connection_errors_are_retried_then_wrapped()
    {
        var handler = new FakeHttpHandler().Always((_, _) => throw new HttpRequestException("connection refused"));
        var time = new FakeTime();

        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => TestClients.History(handler, time).History.SearchAsync(LookupType.UserHid, UserHid));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Contains("connection refused", error.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, time.Delays.Count);
    }

    [Fact]
    public async Task IO_errors_are_connection_errors()
    {
        var handler = new FakeHttpHandler().Always((_, _) => throw new IOException("reset by peer"));

        await Assert.ThrowsAsync<ApiConnectionException>(() => TestClients.History(handler, maxRetries: 0).History.SearchAsync(LookupType.UserHid, UserHid));
    }

    [Fact]
    public async Task Attempt_timeout_raises_timeout_errors_after_retries()
    {
        var handler = new FakeHttpHandler().Always(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var time = new FakeTime();
        var client = new ShieldLabsClient(
            new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, Timeout = TimeSpan.FromMilliseconds(50), MaxRetries = 1 },
            new HttpClient(handler),
            time);

        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() => client.History.SearchAsync(LookupType.UserHid, UserHid));

        Assert.Contains("0.05 s", error.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(time.Delays);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_or_retried()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler().Always(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestClients.History(handler).History.SearchAsync(LookupType.UserHid, UserHid, cancellationToken: cts.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Already_cancelled_token_sends_nothing()
    {
        var handler = new FakeHttpHandler().Always(200, Empty);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TestClients.History(handler).History.SearchAsync(LookupType.UserHid, UserHid, cancellationToken: new CancellationToken(true)));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Client_errors_are_never_retried(int status)
    {
        var handler = new FakeHttpHandler().Always(status, string.Empty, null);

        await Assert.ThrowsAnyAsync<ApiException>(() => TestClients.History(handler, maxRetries: 5).History.SearchAsync(LookupType.UserHid, UserHid));

        Assert.Single(handler.Requests);
    }
}
