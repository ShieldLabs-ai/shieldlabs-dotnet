using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class ManagementClientTests
{
    [Fact]
    public async Task Profile_fixture_maps_to_the_expected_domain_profile()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("management-profile.json"), "application/json; charset=utf-8");
        var expected = Fixtures.Json("management-profile-expected.json");

        var profile = await TestClients.Management(handler).GetProfileAsync();

        Assert.Equal(expected.GetProperty("domain").GetString(), profile.Domain);
        Assert.Equal(expected.GetProperty("remaining_identifications").GetInt64(), profile.RemainingIdentifications);
        Assert.Equal(expected.GetProperty("public_key_masked").GetString(), profile.PublicKeyMasked);
        Assert.Equal(expected.GetProperty("secret_key_masked").GetString(), profile.SecretKeyMasked);
        Assert.NotNull(profile.CreatedAt);
        Assert.Equal(expected.GetProperty("created_at").GetString(), Fixtures.FormatMillis(profile.CreatedAt!.Value));
        Assert.Equal(string.Empty, profile.Raw.GetProperty("Callback").GetString());
    }

    [Fact]
    public async Task Sends_domain_and_secret_headers_to_the_profile_path()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("management-profile.json"));

        await TestClients.Management(handler, domain: "https://WWW.Example.com/").GetProfileAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.shieldlabs.ai/v1/profile", request.Uri.AbsoluteUri);
        Assert.Equal("example.com", request.Header("X-Shield-Domain"));
        Assert.Equal("Bearer " + TestClients.SecretKey, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.StartsWith("shieldlabs-dotnet/" + SdkInfo.Version, request.Header("User-Agent"));
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    [InlineData("https://www.example.com/", "example.com")]
    [InlineData("http://shop.example.com/path/page?x=1#top", "shop.example.com")]
    [InlineData("www.example.com/", "example.com")]
    [InlineData("//www.example.com", "example.com")]
    [InlineData("https://user:pass@example.com", "example.com")]
    [InlineData("example.com:8443", "example.com:8443")]
    [InlineData("wwwexample.com", "wwwexample.com")]
    public void Domain_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, Validation.Domain(input));
        Assert.Equal(expected, TestClients.Management(new FakeHttpHandler(), domain: input).Domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("www.")]
    [InlineData("exa mple.com")]
    [InlineData("exa\tmple.com")]
    [InlineData(null)]
    public void Invalid_domains_are_rejected(string? input)
    {
        Assert.Throws<ValidationException>(() => Validation.Domain(input));
    }

    [Theory]
    [InlineData("b\u00fccher.example")]
    [InlineData("https://www.\u043f\u0440\u0438\u043c\u0435\u0440.example/")]
    public void Internationalized_domains_must_use_punycode(string input)
    {
        var error = Assert.Throws<ValidationException>(() => Validation.Domain(input));

        Assert.Contains("punycode", error.Message);
        Assert.Equal("xn--bcher-kva.example", Validation.Domain("xn--bcher-kva.example"));
    }

    [Fact]
    public void Secret_keys_that_cannot_be_sent_in_a_header_are_rejected()
    {
        var error = Assert.Throws<ValidationException>(
            () => new ManagementClient(new ManagementClientOptions { SecretKey = "9b74c98e1a3f0d2c5b6a7e8f1029384\u00e9", Domain = "example.com" }));

        Assert.Contains("visible ASCII", error.Message);
        Assert.DoesNotContain("9b74", error.Message);
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ValidationException>(() => new ManagementClient(null!));
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { Domain = "example.com" }));
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey }));
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey, Domain = "example.com", BaseUrl = "api.shieldlabs.ai" }));
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey, Domain = "example.com", Timeout = TimeSpan.FromSeconds(-2) }));
        Assert.Throws<ValidationException>(() => new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey, Domain = "example.com", MaxRetries = -1 }));
        Assert.Equal("example.com", new ManagementClient(new ManagementClientOptions { SecretKey = TestClients.SecretKey, Domain = "www.example.com" }).Domain);
    }

    [Fact]
    public async Task Base_url_override_is_used_as_is()
    {
        var handler = new FakeHttpHandler().Enqueue(200, Fixtures.Text("management-profile.json"));

        await TestClients.Management(handler, baseUrl: "http://localhost:9000/").GetProfileAsync();

        Assert.Equal("http://localhost:9000/v1/profile", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Server_busy_is_retried()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(503, "{\"error\":\"server is busy\"}")
            .Enqueue(503, "{\"error\":\"server is busy\"}")
            .Enqueue(200, Fixtures.Text("management-profile.json"));
        var time = new FakeTime();

        var profile = await TestClients.Management(handler, time).GetProfileAsync();

        Assert.Equal("example.com", profile.Domain);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, time.Delays.Count);
    }

    [Fact]
    public async Task Negative_remaining_volume_and_missing_fields_are_tolerated()
    {
        var handler = new FakeHttpHandler().Enqueue(200, "{\"Domain\":\"example.com\",\"Weight\":-120,\"Unknown\":true}");

        var profile = await TestClients.Management(handler).GetProfileAsync();

        Assert.Equal(-120, profile.RemainingIdentifications);
        Assert.Equal(string.Empty, profile.PublicKeyMasked);
        Assert.Null(profile.CreatedAt);
        Assert.True(profile.Raw.GetProperty("Unknown").GetBoolean());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task Unexpected_success_bodies_raise_api_errors(string body)
    {
        var handler = new FakeHttpHandler().Enqueue(200, body);

        var error = await Assert.ThrowsAsync<ApiException>(() => TestClients.Management(handler).GetProfileAsync());

        Assert.Equal(200, error.StatusCode);
    }

    [Fact]
    public async Task Zero_created_at_is_kept()
    {
        var handler = new FakeHttpHandler().Enqueue(200, "{\"Domain\":\"example.com\",\"Weight\":0,\"CreatedAt\":\"0001-01-01T00:00:00Z\"}");

        var profile = await TestClients.Management(handler).GetProfileAsync();

        Assert.Equal(DateTimeOffset.MinValue, profile.CreatedAt);
    }
}
