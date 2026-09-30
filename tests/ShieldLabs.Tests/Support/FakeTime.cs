using ShieldLabs.Internal;

namespace ShieldLabs.Tests.Support;

/// <summary>
/// A virtual clock: delays return at once and advance the clock, and are recorded. Attempt
/// timeouts are recorded as well.
/// </summary>
internal sealed class FakeTime : ITimeSource
{
    public TimeSpan Elapsed { get; private set; }

    public DateTimeOffset UtcNow { get; private set; } = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = new();

    /// <summary>The timeout of every HTTP attempt, in order.</summary>
    public List<TimeSpan> AttemptTimeouts { get; } = new();

    /// <summary>How much earlier than asked a delay returns, like a timer that fires early.</summary>
    public TimeSpan EarlyBy { get; set; }

    /// <summary>Value returned by <see cref="NextJitter"/>; 1.0 makes backoff delays exact.</summary>
    public double Jitter { get; set; } = 1.0;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay > EarlyBy ? delay - EarlyBy : TimeSpan.Zero);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the attempt timeout and still enforces it on the real clock, so a handler that never
    /// answers is cancelled.
    /// </summary>
    public void CancelAfter(CancellationTokenSource source, TimeSpan delay)
    {
        AttemptTimeouts.Add(delay);
        SystemTimeSource.Instance.CancelAfter(source, delay);
    }

    /// <summary>
    /// Answers the current attempt the way an API that never responds does: the whole attempt
    /// timeout passes on the virtual clock, then the attempt is cancelled.
    /// </summary>
    public Task<HttpResponseMessage> TimeOutAttempt()
    {
        Advance(AttemptTimeouts[^1]);
        throw new TaskCanceledException("The attempt timed out.");
    }

    public double NextJitter() => Jitter;

    public void Advance(TimeSpan by)
    {
        Elapsed += by;
        UtcNow += by;
    }
}

/// <summary>Builds clients wired to a fake handler and a virtual clock.</summary>
internal static class TestClients
{
    public const string ApiKey = "sec_a1b2c3d4-e5f6a7b8-c9d0e1f2";
    public const string SecretKey = "9b74c98e1a3f0d2c5b6a7e8f10293847";

    public static ShieldLabsClient History(FakeHttpHandler handler, FakeTime? time = null, int maxRetries = 2, string? baseUrl = null)
        => new ShieldLabsClient(
            new ShieldLabsClientOptions { ApiKey = ApiKey, BaseUrl = baseUrl, MaxRetries = maxRetries },
            new HttpClient(handler),
            time ?? new FakeTime());

    public static ManagementClient Management(FakeHttpHandler handler, FakeTime? time = null, int maxRetries = 2, string domain = "example.com", string? baseUrl = null)
        => new ManagementClient(
            new ManagementClientOptions { SecretKey = SecretKey, Domain = domain, BaseUrl = baseUrl, MaxRetries = maxRetries },
            new HttpClient(handler),
            time ?? new FakeTime());

    /// <summary>A History API body with the given rows (raw JSON objects) and total.</summary>
    public static string Page(IEnumerable<string> rows, long total) => "{\"data\":[" + string.Join(",", rows) + "],\"total\":" + total + "}";

    /// <summary>A minimal History row for paging tests.</summary>
    public static string Row(string requestId, int score = 10)
        => "{\"request_id\":\"" + requestId + "\",\"session_id\":\"5a6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d\",\"cookie_id\":\"6b7c8d9e-0f1a-4b2c-9d3e-4f5a6b7c8d9e\","
           + "\"domain\":\"example.com\",\"user_hid\":\"anonymous\",\"device_id\":\"7c8d9e0f-1a2b-5c3d-8e4f-5a6b7c8d9e0f\","
           + "\"visitor_id\":\"8d9e0f1a-2b3c-5d4e-9f5a-6b7c8d9e0f1a\",\"ip\":\"198.51.100.66\",\"country\":\"Germany\",\"score\":" + score
           + ",\"score_details\":\"\",\"created_at\":\"2026-09-30 13:20:30.250\"}";

    /// <summary>A UUIDv4-shaped request ID for index <paramref name="n"/>.</summary>
    public static string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";
}
