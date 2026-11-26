using Nomad.Core.Hub;

namespace Nomad.Core.Tests;

/// <summary>The phone's enrollment link carries the hub address and the token URL-encoded, and flags plain HTTP.</summary>
public sealed class PhoneEnrollLinkTests
{
    [Fact]
    public void BothValuesAreUrlEncoded() =>
        Assert.Equal("nomad://enroll?hub=https%3A%2F%2Fhub.example.com%3A8443%2Fnomad%2F&token=a-b_c%2Bd%3D%26e",
            PhoneEnrollLink.Make("https://hub.example.com:8443/nomad/", "a-b_c+d=&e"));

    [Fact]
    public void TheLinkReadsBackToTheSameValues()
    {
        var link = new Uri(PhoneEnrollLink.Make("https://pc.tail1234.ts.net/", "Qm9va3M_YXJl-IGE"));
        var query = link.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        Assert.Equal("nomad", link.Scheme);
        Assert.Equal("enroll", link.Host);
        Assert.Equal("https://pc.tail1234.ts.net/", query["hub"]);
        Assert.Equal("Qm9va3M_YXJl-IGE", query["token"]);
    }

    [Theory]
    [InlineData("https://hub.example.com/", false)]
    [InlineData("HTTPS://hub.example.com/", false)]
    [InlineData("http://127.0.0.1:8095/", true)]
    [InlineData("not an address", true)]
    public void PlainHttpNeedsHttps(string hubUrl, bool needs) => Assert.Equal(needs, PhoneEnrollLink.NeedsHttps(hubUrl));
}
