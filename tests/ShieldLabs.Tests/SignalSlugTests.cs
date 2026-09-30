using ShieldLabs.Internal;
using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class SignalSlugTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Fixtures.Json("signal-slug-cases.json").GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("description").GetString()!, c.GetProperty("slug").GetString()!);
        }

        return data;
    }

    [Fact]
    public void Fixture_has_all_cases()
    {
        Assert.Equal(31, Cases().Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Maps_description_to_slug(string description, string slug)
    {
        Assert.Equal(slug, Normalizer.SignalSlug(description));
    }

    [Theory]
    [InlineData("Tab\there", "tabhere")]
    [InlineData("a//b--c  d", "a_b_c_d")]
    [InlineData("_x_", "x")]
    [InlineData("Déjà Vu 42", "déjà_vu_42")]
    [InlineData("\U0001D400bc", "\U0001D400bc")]
    [InlineData("Sticky verdict: Is VPN (request 11111111-2222-4333-8444-555555555555)", "is_vpn")]
    public void Fallback_slugger_rules(string description, string slug)
    {
        Assert.Equal(slug, Normalizer.SignalSlug(description));
    }
}
