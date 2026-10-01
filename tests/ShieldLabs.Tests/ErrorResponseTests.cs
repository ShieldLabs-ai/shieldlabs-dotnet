using System.Text.Json;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class ErrorResponseTests
{
    private const int MaxRetries = 2;

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        var count = Fixtures.Json("error-responses.json").GetProperty("cases").GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static Type ExpectedType(string name) => name switch
    {
        "BadRequestError" => typeof(BadRequestException),
        "AuthenticationError" => typeof(AuthenticationException),
        "QuotaExceededError" => typeof(QuotaExceededException),
        "NotFoundError" => typeof(NotFoundException),
        "RateLimitError" => typeof(RateLimitException),
        "ServerError" => typeof(ServerException),
        _ => throw new InvalidOperationException("Unmapped error class " + name),
    };

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public async Task Maps_status_to_exception_and_retries_as_specified(int index)
    {
        var c = Fixtures.Json("error-responses.json").GetProperty("cases")[index];
        var surface = c.GetProperty("surface").GetString();
        var status = c.GetProperty("status").GetInt32();
        var body = c.GetProperty("body").GetString()!;
        var contentType = c.GetProperty("content_type").ValueKind == JsonValueKind.Null ? null : c.GetProperty("content_type").GetString();
        var retried = c.GetProperty("retry").GetBoolean();
        var handler = new FakeHttpHandler().Always(status, body, contentType);
        var time = new FakeTime();

        Func<Task> call = surface == "history"
            ? () => TestClients.History(handler, time, MaxRetries).History.SearchAsync(LookupType.RequestId, "a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d")
            : () => TestClients.Management(handler, time, MaxRetries).GetProfileAsync();

        var error = await Assert.ThrowsAnyAsync<ApiException>(call);

        Assert.IsType(ExpectedType(c.GetProperty("expected_error").GetString()!), error);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(body, error.Body);
        Assert.Equal(retried ? 1 + MaxRetries : 1, handler.Requests.Count);
        Assert.Equal(retried ? MaxRetries : 0, time.Delays.Count);
        Assert.Contains("HTTP " + status, error.Message);
    }

    [Fact]
    public void Fixture_covers_both_surfaces_and_402()
    {
        var cases = Fixtures.Json("error-responses.json").GetProperty("cases").EnumerateArray().ToList();
        Assert.Contains(cases, c => c.GetProperty("surface").GetString() == "history");
        Assert.Contains(cases, c => c.GetProperty("surface").GetString() == "management");
        Assert.Contains(cases, c => c.GetProperty("expected_error").GetString() == "QuotaExceededError");
    }

    [Theory]
    [InlineData("{\"error\":\"invalid api key\"}\n", "invalid api key")]
    [InlineData("\"fail parse uuid\"", "fail parse uuid")]
    [InlineData("null", null)]
    [InlineData("", null)]
    [InlineData("404 page not found", null)]
    [InlineData("<html><body><h1>502 Bad Gateway</h1></body></html>", null)]
    [InlineData("{\"message\":\"other shape\"}", null)]
    [InlineData("{\"error\":42}", null)]
    public async Task Error_text_is_parsed_defensively(string body, string? expected)
    {
        var handler = new FakeHttpHandler().Always(400, body, "text/plain; charset=utf-8");

        var error = await Assert.ThrowsAsync<BadRequestException>(() => TestClients.History(handler).History.SearchAsync(LookupType.UserHid, "anonymous"));

        Assert.Equal(expected, error.Error);
        Assert.Equal(body, error.Body);
    }

    [Fact]
    public async Task Response_headers_are_exposed()
    {
        var handler = new FakeHttpHandler().Always(503, "{\"error\":\"server is busy\"}", "application/json; charset=utf-8", r => r.Headers.TryAddWithoutValidation("X-Request-Trace", "abc"));

        var error = await Assert.ThrowsAsync<ServerException>(() => TestClients.Management(handler, maxRetries: 0).GetProfileAsync());

        Assert.Equal("abc", Assert.Single(error.Headers["x-request-trace"]));
        Assert.Equal("application/json; charset=utf-8", Assert.Single(error.Headers["Content-Type"]));
        Assert.Equal("server is busy", error.Error);
    }

    [Theory]
    [InlineData(405, typeof(ApiException))]
    [InlineData(409, typeof(ApiException))]
    [InlineData(504, typeof(ServerException))]
    public async Task Other_statuses_map_to_the_base_or_server_class(int status, Type expected)
    {
        var handler = new FakeHttpHandler().Always(status, string.Empty, null);

        var error = await Assert.ThrowsAnyAsync<ApiException>(() => TestClients.History(handler, maxRetries: 0).History.SearchAsync(LookupType.UserHid, "anonymous"));

        Assert.IsType(expected, error);
    }

    [Fact]
    public async Task Messages_carry_actionable_hints()
    {
        var history401 = await Assert.ThrowsAsync<AuthenticationException>(
            () => TestClients.History(new FakeHttpHandler().Always(401, "{\"error\":\"invalid api key\"}\n")).History.SearchAsync(LookupType.UserHid, "anonymous"));
        Assert.Contains("Private API Key", history401.Message);

        var management401 = await Assert.ThrowsAsync<AuthenticationException>(
            () => TestClients.Management(new FakeHttpHandler().Always(401, string.Empty, null)).GetProfileAsync());
        Assert.Contains("Secret Key", management401.Message);

        var management429 = await Assert.ThrowsAsync<RateLimitException>(
            () => TestClients.Management(new FakeHttpHandler().Always(429, "{\"error\":\"too many requests\"}")).GetProfileAsync());
        Assert.Contains("10 minutes", management429.Message);
    }

    [Fact]
    public void Exceptions_share_the_base_class()
    {
        Assert.IsAssignableFrom<ShieldLabsException>(new ApiConnectionException("x"));
        Assert.IsAssignableFrom<ShieldLabsException>(new ApiTimeoutException("x"));
        Assert.IsAssignableFrom<ShieldLabsException>(new SignatureVerificationException("x"));
        Assert.IsAssignableFrom<ShieldLabsException>(new WebhookParseException("x"));
        Assert.IsAssignableFrom<ShieldLabsException>(new ValidationException("x"));
        Assert.IsAssignableFrom<ApiException>(new BadRequestException("x"));
        Assert.IsAssignableFrom<ApiException>(new QuotaExceededException("x"));
        Assert.IsAssignableFrom<ApiException>(new NotFoundException("x"));
        Assert.IsAssignableFrom<ApiException>(new ServerException("x"));
        Assert.Equal(401, new AuthenticationException("x").StatusCode);
        Assert.Equal(429, new RateLimitException("x").StatusCode);
        Assert.Null(new RateLimitException("x").RetryAfter);
        Assert.Empty(new ApiException("x", 418).Headers);
        Assert.Equal(string.Empty, new ApiException("x", 418).Body);
        var inner = new InvalidOperationException("inner");
        Assert.Same(inner, new ShieldLabsException("x", inner).InnerException);
    }
}
