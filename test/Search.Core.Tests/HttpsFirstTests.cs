using Search.Core;

namespace Search.Core.Tests;

public class HttpsFirstTests
{
    private static string? Upgraded(string address) => HttpsFirst.Upgrade(new Uri(address))?.AbsoluteUri;

    [Fact]
    public void AnHttpPageIsTriedOverHttps()
    {
        Assert.Equal("https://example.com/a?b=1#c", Upgraded("http://example.com/a?b=1#c"));
        Assert.Equal("https://www.example.co.uk/", Upgraded("http://www.example.co.uk"));
        Assert.Equal("https://example.com/", Upgraded("http://example.com:80/"));
        Assert.Equal(("https", "xn--bcher-kva.example"), HttpsFirst.Upgrade(new Uri("http://bücher.example/")) is { } idn ? (idn.Scheme, idn.IdnHost) : default);
        Assert.Equal("https://user@example.com/", Upgraded("http://user@example.com/"));
    }

    [Fact]
    public void WhatIsLeftAsAsked()
    {
        foreach (var address in new[] { "https://example.com/", "ftp://example.com/", "file:///C:/a.txt", "about:blank", "http://example.com:8080/" })
            Assert.Null(Upgraded(address));
    }

    /// The Mac's local rule: a dev server rarely has HTTPS, and no certificate names an IP address.
    [Fact]
    public void LocalAddressesAreLeftAlone()
    {
        foreach (var address in new[] { "http://localhost/", "http://LOCALHOST/", "http://app.localhost/", "http://site.test/", "http://printer.local/", "http://printer.local./",
                     "http://127.0.0.1/", "http://192.168.1.10/", "http://[::1]/", "http://[fe80::1]/", "http://intranet/", "http://router/" })
        {
            Assert.True(HttpsFirst.Local(new Uri(address)), address);
            Assert.Null(Upgraded(address));
        }
        foreach (var address in new[] { "http://example.com/", "http://testing.com/", "http://local.example/", "http://localhost.example.com/", "http://1password.com/" })
            Assert.False(HttpsFirst.Local(new Uri(address)), address);
    }
}
