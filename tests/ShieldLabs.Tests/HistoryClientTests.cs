using System.Text.Json.Nodes;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class HistoryClientTests
{
    private const string DeviceId = "d8e0f2a4-b6c8-4d0e-bf2a-4b6c8d0e2f4a";

    [Fact]
    public async Task Search_parses_the_history_page_fixture()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-page.json"));
        var client = TestClients.History(handler);

        var page = await client.History.SearchAsync(LookupType.DeviceId, DeviceId);

        Assert.Equal(37, page.Total);
        var expected = Fixtures.NormalizationCases().Where(c => c.GetProperty("source").GetString() == "history").ToList();
        Assert.Equal(expected.Count, page.Data.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Fixtures.AssertJsonEqual(JsonNode.Parse(expected[i].GetProperty("expected").GetRawText()), Fixtures.Canonical(page.Data[i]), "row " + i);
        }
    }

    [Fact]
    public async Task Search_parses_the_empty_fixture()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-empty.json"));

        var page = await TestClients.History(handler).History.SearchAsync(LookupType.UserHid, "anonymous");

        Assert.Empty(page.Data);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Search_sends_the_documented_request()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-empty.json"));

        await TestClients.History(handler).History.SearchAsync(LookupType.DeviceId, DeviceId.ToUpperInvariant());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://account.shieldlabs.ai/api/v1/history/device_id/" + DeviceId + "?limit=20&offset=0", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + TestClients.ApiKey, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.StartsWith("shieldlabs-dotnet/" + ShieldLabs.Internal.SdkInfo.Version, request.Header("User-Agent"));
    }

    [Theory]
    [InlineData(LookupType.Ip, "203.0.113.24", "ip/203.0.113.24")]
    [InlineData(LookupType.UserHid, "9f86d081884c7d659a2feaa0c55ad015", "user_hid/9f86d081884c7d659a2feaa0c55ad015")]
    [InlineData(LookupType.UserHid, "user 42?x=1#frag %", "user_hid/user%2042%3Fx=1%23frag%20%25")]
    [InlineData(LookupType.VisitorId, "E9F1A3B5-C7D9-4E1F-8A3B-5C7D9E1F3A5B", "visitor_id/e9f1a3b5-c7d9-4e1f-8a3b-5c7d9e1f3a5b")]
    [InlineData(LookupType.RequestId, "a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d", "request_id/a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d")]
    [InlineData(LookupType.SessionId, "00000000-0000-0000-0000-000000000000", "session_id/00000000-0000-0000-0000-000000000000")]
    [InlineData(LookupType.CookieId, "c7d9e1f3-a5b7-4c9d-ae1f-3a5b7c9d1e3f", "cookie_id/c7d9e1f3-a5b7-4c9d-ae1f-3a5b7c9d1e3f")]
    public async Task Search_builds_the_path_for_every_type(LookupType type, string value, string expectedPath)
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-empty.json"));

        await TestClients.History(handler).History.SearchAsync(type, value, new HistorySearchOptions { Limit = 100, Offset = 40 });

        Assert.Equal("/api/v1/history/" + expectedPath, Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal("?limit=100&offset=40", handler.Requests[0].Uri.Query);
    }

    [Theory]
    // The History API matches a User HID only in this canonical escaping: ASCII letters, digits and
    // "-._~$&+,:;=@" as they are, every other UTF-8 byte as uppercase %XX.
    [InlineData("anonymous", "anonymous")]
    [InlineData("a@b", "a@b")]
    [InlineData("a+b", "a+b")]
    [InlineData("a:b=c", "a:b=c")]
    [InlineData("x$y&z", "x$y&z")]
    [InlineData("a,b", "a,b")]
    [InlineData("a;b", "a;b")]
    [InlineData("-._~", "-._~")]
    [InlineData("...", "...")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("a b", "a%20b")]
    [InlineData("\u00fc", "%C3%BC")]
    [InlineData("a%2Fb", "a%252Fb")]
    [InlineData("a!b c", "a%21b%20c")]
    [InlineData("it's (1)*", "it%27s%20%281%29%2A")]
    [InlineData("Team A?c#d%e \u00e9", "Team%20A%3Fc%23d%25e%20%C3%A9")]
    [InlineData("\"<>[]^`{|}\\", "%22%3C%3E%5B%5D%5E%60%7B%7C%7D%5C")]
    [InlineData("line\nbreak", "line%0Abreak")]
    [InlineData("\U0001F600", "%F0%9F%98%80")]
    public async Task User_hid_uses_the_escaping_the_history_api_matches(string userHid, string segment)
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-empty.json"));

        await TestClients.History(handler).History.SearchAsync(LookupType.UserHid, userHid);

        Assert.Equal("/api/v1/history/user_hid/" + segment + "?limit=20&offset=0", Assert.Single(handler.Requests).Uri.PathAndQuery);
    }

    [Theory]
    [InlineData(".", "cannot be searched")]
    [InlineData("..", "cannot be searched")]
    [InlineData("a/b", "contains \"/\"")]
    [InlineData("/", "contains \"/\"")]
    [InlineData("acct/", "contains \"/\"")]
    [InlineData("../anonymous", "contains \"/\"")]
    public async Task User_hids_the_history_api_cannot_match_send_nothing(string userHid, string message)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));
        var client = TestClients.History(handler);

        var error = await Assert.ThrowsAsync<ValidationException>(() => client.History.SearchAsync(LookupType.UserHid, userHid));
        Assert.Contains(message, error.Message);
        Assert.Throws<ValidationException>(() => client.History.IterateAsync(LookupType.UserHid, userHid));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task User_hid_with_an_unpaired_surrogate_sends_nothing()
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => TestClients.History(handler).History.SearchAsync(LookupType.UserHid, "user" + '\ud800'));

        Assert.Contains("unpaired surrogate", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task User_hid_goes_on_the_wire_in_canonical_form()
    {
        using var server = new LoopbackServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient();
        var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, BaseUrl = server.BaseUrl }, http);

        var requestLine = server.AnswerOnceAsync(Fixtures.Text("history-empty.json"), cts.Token);
        var page = await client.History.SearchAsync(LookupType.UserHid, "a@b+c=d \u00e9!", cancellationToken: cts.Token);

        Assert.Equal("GET /api/v1/history/user_hid/a@b+c=d%20%C3%A9%21?limit=20&offset=0 HTTP/1.1", await requestLine);
        Assert.Empty(page.Data);
    }

    /// <summary>
    /// Characters of a User HID the canonical path form keeps as they are: ASCII letters and digits,
    /// the unreserved marks and the sub-delimiters a path allows. Every other byte is percent-encoded
    /// with uppercase hex digits.
    /// </summary>
    private const string CanonicalKept = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~$&+,:;=@";

    private static string CanonicalEscape(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            if (b < 0x80 && CanonicalKept.IndexOf((char)b) >= 0)
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Every_ascii_character_of_a_user_hid_is_escaped_in_canonical_form()
    {
        for (var code = 0; code < 0x80; code++)
        {
            if (code == '/')
            {
                continue;
            }

            var userHid = "u" + (char)code;
            var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("history-empty.json"));

            await TestClients.History(handler).History.SearchAsync(LookupType.UserHid, userHid);

            var expected = "/api/v1/history/user_hid/" + CanonicalEscape(userHid) + "?limit=20&offset=0";
            Assert.True(
                expected == Assert.Single(handler.Requests).Uri.PathAndQuery,
                $"U+{code:X4}: expected {expected}, sent {handler.Requests[0].Uri.PathAndQuery}");
        }
    }

    [Fact]
    public async Task Printable_ascii_user_hid_goes_on_the_wire_in_canonical_form()
    {
        var chars = new System.Text.StringBuilder();
        for (var c = ' '; c <= '~'; c++)
        {
            if (c != '/')
            {
                chars.Append(c);
            }
        }

        var userHid = chars.ToString();
        using var server = new LoopbackServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient();
        var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, BaseUrl = server.BaseUrl }, http);

        var requestLine = server.AnswerOnceAsync(Fixtures.Text("history-empty.json"), cts.Token);
        await client.History.SearchAsync(LookupType.UserHid, userHid, cancellationToken: cts.Token);

        Assert.Equal(
            "GET /api/v1/history/user_hid/%20%21%22%23$%25&%27%28%29%2A+,-.0123456789:;%3C=%3E%3F@ABCDEFGHIJKLMNOPQRSTUVWXYZ%5B%5C%5D%5E_%60abcdefghijklmnopqrstuvwxyz%7B%7C%7D~?limit=20&offset=0 HTTP/1.1",
            await requestLine);
        Assert.Equal("/api/v1/history/user_hid/" + CanonicalEscape(userHid) + "?limit=20&offset=0", (await requestLine).Split(' ')[1]);
    }

    [Theory]
    [InlineData(LookupType.DeviceId, "not-a-uuid")]
    [InlineData(LookupType.DeviceId, "")]
    [InlineData(LookupType.DeviceId, "d8e0f2a4b6c84d0ebf2a4b6c8d0e2f4a")]
    [InlineData(LookupType.DeviceId, "{d8e0f2a4-b6c8-4d0e-bf2a-4b6c8d0e2f4a}")]
    [InlineData(LookupType.DeviceId, "d8e0f2a4-b6c8-4d0e-bf2a-4b6c8d0e2f4a\n")]
    [InlineData(LookupType.RequestId, "z8e0f2a4-b6c8-4d0e-bf2a-4b6c8d0e2f4a")]
    [InlineData(LookupType.VisitorId, "d8e0f2a4-b6c8-4d0e-bf2a_4b6c8d0e2f4a")]
    [InlineData(LookupType.Ip, "2001:db8::1")]
    [InlineData(LookupType.Ip, "256.1.1.1")]
    [InlineData(LookupType.Ip, "1.2.3")]
    [InlineData(LookupType.Ip, "1.2.3.4.5")]
    [InlineData(LookupType.Ip, "01.2.3.4")]
    [InlineData(LookupType.Ip, "1.2.3.x")]
    [InlineData(LookupType.Ip, "1..3.4")]
    [InlineData(LookupType.Ip, "1234.2.3.4")]
    [InlineData(LookupType.UserHid, "")]
    public async Task Invalid_values_throw_before_any_request(LookupType type, string value)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));
        var client = TestClients.History(handler);

        await Assert.ThrowsAsync<ValidationException>(() => client.History.SearchAsync(type, value));
        Assert.Throws<ValidationException>(() => client.History.IterateAsync(type, value));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Null_value_and_unknown_type_throw_before_any_request()
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));
        var client = TestClients.History(handler);

        await Assert.ThrowsAsync<ValidationException>(() => client.History.SearchAsync(LookupType.UserHid, null!));
        var unknown = await Assert.ThrowsAsync<ValidationException>(() => client.History.SearchAsync((LookupType)42, "anonymous"));
        Assert.Contains("Unknown lookup type", unknown.Message);
        Assert.Throws<ValidationException>(() => client.History.IterateAsync((LookupType)42, "anonymous"));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 0)]
    [InlineData(-1, 0)]
    [InlineData(20, -1)]
    public async Task Invalid_paging_throws_before_any_request(int limit, int offset)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));

        await Assert.ThrowsAsync<ValidationException>(
            () => TestClients.History(handler).History.SearchAsync(LookupType.DeviceId, DeviceId, new HistorySearchOptions { Limit = limit, Offset = offset }));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Limit_bounds_are_accepted(int limit)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));

        await TestClients.History(handler).History.SearchAsync(LookupType.DeviceId, DeviceId, new HistorySearchOptions { Limit = limit });

        Assert.Equal($"?limit={limit}&offset=0", Assert.Single(handler.Requests).Uri.Query);
    }

    [Theory]
    [InlineData(null, "https://account.shieldlabs.ai/api/v1/history/")]
    [InlineData("https://account.shieldlabs.ai", "https://account.shieldlabs.ai/api/v1/history/")]
    [InlineData("https://account.shieldlabs.ai/", "https://account.shieldlabs.ai/api/v1/history/")]
    [InlineData("https://account.shieldlabs.ai/api", "https://account.shieldlabs.ai/api/v1/history/")]
    [InlineData("https://account.shieldlabs.ai/api/", "https://account.shieldlabs.ai/api/v1/history/")]
    [InlineData(" https://dev.account.shieldlabs.ai/API ", "https://dev.account.shieldlabs.ai/api/v1/history/")]
    [InlineData("http://localhost:8080/proxy/api", "http://localhost:8080/proxy/api/v1/history/")]
    public async Task Base_url_is_an_origin_and_a_trailing_api_is_removed(string? baseUrl, string expectedPrefix)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));
        var client = TestClients.History(handler, baseUrl: baseUrl);

        await client.History.SearchAsync(LookupType.DeviceId, DeviceId);

        Assert.StartsWith(expectedPrefix, Assert.Single(handler.Requests).Uri.AbsoluteUri);
        Assert.DoesNotContain("/api/api/", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://account.shieldlabs.ai")]
    [InlineData("https://account.shieldlabs.ai/?x=1")]
    [InlineData("https://account.shieldlabs.ai/#top")]
    [InlineData("/api/v1")]
    public void Invalid_base_urls_are_rejected(string baseUrl)
    {
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, BaseUrl = baseUrl }));
    }

    [Theory]
    [InlineData("http://account.shieldlabs.ai")]
    [InlineData("http://dev.account.shieldlabs.ai/api")]
    [InlineData("http://203.0.113.24:8080")]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://localhost.example.com")]
    public void Plain_http_is_rejected_for_hosts_other_than_loopback(string baseUrl)
    {
        var error = Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, BaseUrl = baseUrl }));
        Assert.Contains("https", error.Message);
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey, Domain = "example.com", BaseUrl = baseUrl }));
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://LOCALHOST:8080/", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:9000", "http://127.0.0.1:9000")]
    [InlineData("http://[::1]:9000", "http://[::1]:9000")]
    [InlineData("http://127.10.0.1", "http://127.10.0.1")]
    [InlineData("https://account.shieldlabs.ai", "https://account.shieldlabs.ai")]
    public void Plain_http_is_accepted_for_loopback_hosts(string baseUrl, string expected)
    {
        Assert.Equal(expected, new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, BaseUrl = baseUrl }).BaseUrl);
    }

    [Fact]
    public void Client_options_are_validated()
    {
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(null!));
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions()));
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = "   " }));
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = "sec_a\u0001" }));
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, Timeout = TimeSpan.Zero }));
        Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, MaxRetries = -1 }));

        var client = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = TestClients.ApiKey, Timeout = Timeout.InfiniteTimeSpan });
        Assert.Equal("https://account.shieldlabs.ai", client.BaseUrl);
        Assert.NotNull(client.History);
        Assert.NotNull(client.Identifications);
    }

    [Theory]
    [InlineData("sec_a1b2c3d4-e5f6a7b8-c9d0e1f\u00e9")]
    [InlineData("sec_a1b2c3d4 e5f6a7b8-c9d0e1f2")]
    [InlineData("sec_\u0441ecret-key")]
    public void Keys_that_cannot_be_sent_in_a_header_are_rejected_without_echo(string apiKey)
    {
        var error = Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = apiKey }));

        Assert.Contains("visible ASCII", error.Message);
        Assert.DoesNotContain("sec_", error.Message);
    }

    [Fact]
    public void Error_message_for_an_empty_key_never_echoes_input()
    {
        var error = Assert.Throws<ValidationException>(() => new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = "sec_\u0007secret" }));
        Assert.DoesNotContain("secret", error.Message.Replace("ApiKey", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_with_surrounding_whitespace_is_trimmed_and_legacy_shapes_are_accepted()
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-empty.json"));
        var trimmed = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = " " + TestClients.ApiKey + "\n" }, new HttpClient(handler), new FakeTime());
        var legacy = new ShieldLabsClient(new ShieldLabsClientOptions { ApiKey = "legacy-key-shape" }, new HttpClient(handler), new FakeTime());

        await trimmed.History.SearchAsync(LookupType.DeviceId, DeviceId);
        await legacy.History.SearchAsync(LookupType.DeviceId, DeviceId);

        Assert.Equal("Bearer " + TestClients.ApiKey, handler.Requests[0].Header("Authorization"));
        Assert.Equal("Bearer legacy-key-shape", handler.Requests[1].Header("Authorization"));
    }

    [Theory]
    [InlineData("sec_a1b2c3d4-e5f6a7b8-c9d0e1f2", true)]
    [InlineData("sec_A1b2c3d4-e5f6a7b8-c9d0e1f2", false)]
    [InlineData("sec_a1b2c3d4-e5f6a7b8-c9d0e1f", false)]
    [InlineData("sec_a1b2c3d4_e5f6a7b8-c9d0e1f2", false)]
    [InlineData("pk_a1b2c3d4-e5f6a7b8-c9d0e1f2x", false)]
    public void Private_api_key_shape_check(string key, bool expected)
    {
        Assert.Equal(expected, ShieldLabs.Internal.Validation.LooksLikePrivateApiKey(key));
    }

    [Fact]
    public async Task Unexpected_success_bodies_raise_api_errors()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, "<html>proxy</html>", "text/html")
            .Enqueue(200, "[]")
            .Enqueue(200, "{\"total\":3}")
            .Enqueue(200, "{\"data\":[{\"request_id\":\"a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d\"},\"junk\",1]}");
        var client = TestClients.History(handler);

        var notJson = await Assert.ThrowsAsync<ApiException>(() => client.History.SearchAsync(LookupType.DeviceId, DeviceId));
        Assert.Equal(200, notJson.StatusCode);
        Assert.Equal("<html>proxy</html>", notJson.Body);
        await Assert.ThrowsAsync<ApiException>(() => client.History.SearchAsync(LookupType.DeviceId, DeviceId));

        var noData = await client.History.SearchAsync(LookupType.DeviceId, DeviceId);
        Assert.Empty(noData.Data);
        Assert.Equal(3, noData.Total);

        var noTotal = await client.History.SearchAsync(LookupType.DeviceId, DeviceId);
        Assert.Equal("a5b7c9d1-e3f5-4a7b-9c1d-3e5f7a9b1c3d", Assert.Single(noTotal.Data).RequestId);
        Assert.Equal(1, noTotal.Total);
    }
}
