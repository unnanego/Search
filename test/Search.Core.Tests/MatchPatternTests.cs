using Search.Core;

namespace Search.Core.Tests;

public class MatchPatternTests
{
    private static MatchPattern P(string text) => MatchPattern.Parse(text) ?? throw new InvalidOperationException($"Didn't parse: {text}");

    [Fact]
    public void AllUrlsReads()
    {
        var all = P("<all_urls>");
        Assert.True(all.AllUrls);
        Assert.True(all.AllHosts);
        Assert.Equal("<all_urls>", all.ToString());
    }

    [Fact]
    public void APatternsPartsRead()
    {
        var p = P("HTTPS://*.Example.com:8443/mail/*");
        Assert.Equal(("https", "*.example.com", "8443", "/mail/*"), (p.Scheme, p.Host, p.Port, p.Path));
        Assert.Equal("HTTPS://*.Example.com:8443/mail/*", p.Text);
        Assert.False(p.AllHosts);
    }

    [Fact]
    public void AFilePatternHasNoHost()
    {
        var p = P("file:///*");
        Assert.Equal(("file", "", "/*"), (p.Scheme, p.Host, p.Path));
        Assert.False(p.Web);
        Assert.False(p.AllHosts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("example.com/*")]
    [InlineData("gopher://example.com/*")]
    [InlineData("https:///*")]
    [InlineData("https://*foo.com/*")]
    [InlineData("https://foo.*.com/*")]
    [InlineData("https://*.*.com/*")]
    [InlineData("https://example.com:http/*")]
    [InlineData("https://example.com:/*")]
    [InlineData("tabs")]
    public void WhatChromeWouldntTakeIsNoPattern(string text)
    {
        Assert.Null(MatchPattern.Parse(text));
        Assert.False(MatchPattern.IsPattern(text));
    }

    [Fact] public void NullIsNoPattern() => Assert.Null(MatchPattern.Parse(null));

    [Theory]
    [InlineData("<all_urls>", "https://example.com/", true)]
    [InlineData("<all_urls>", "http://a.b.c/x?y", true)]
    [InlineData("<all_urls>", "file:///C:/notes.txt", true)]
    [InlineData("<all_urls>", "wss://example.com/socket", true)]
    [InlineData("<all_urls>", "chrome-extension://abc/page.html", false)]
    [InlineData("<all_urls>", "about:blank", false)]
    [InlineData("*://*/*", "http://example.com/", true)]
    [InlineData("*://*/*", "https://example.com/a/b", true)]
    [InlineData("*://*/*", "ws://example.com/", true)]
    [InlineData("*://*/*", "wss://example.com/", true)]
    [InlineData("*://*/*", "file:///C:/x", false)]
    [InlineData("*://*/*", "ftp://example.com/", false)]
    [InlineData("http://*/*", "ws://example.com/", false)]
    [InlineData("https://*.example.com/path*", "https://example.com/path", true)]
    [InlineData("https://*.example.com/path*", "https://a.b.example.com/pathway?q=1", true)]
    [InlineData("https://*.example.com/path*", "https://EXAMPLE.com/path", true)]
    [InlineData("https://*.example.com/path*", "https://notexample.com/path", false)]
    [InlineData("https://*.example.com/path*", "https://example.com.evil/path", false)]
    [InlineData("https://*.example.com/path*", "http://example.com/path", false)]
    [InlineData("https://*.example.com/path*", "https://example.com/other", false)]
    [InlineData("https://example.com/*", "https://www.example.com/", false)]
    [InlineData("https://example.com/p?q=*", "https://example.com/p?q=1", true)]
    [InlineData("https://example.com/p?q", "https://example.com/pxq", false)]
    [InlineData("https://example.com/*/end", "https://example.com/a/b/end", true)]
    [InlineData("http://localhost:8080/*", "http://localhost:8080/x", true)]
    [InlineData("http://localhost:8080/*", "http://localhost/x", false)]
    [InlineData("http://example.com:80/*", "http://example.com/", true)]
    [InlineData("http://example.com:*/*", "http://example.com:9000/", true)]
    [InlineData("http://example.com/*", "http://example.com:9000/", true)]
    [InlineData("file:///*", "file:///C:/x/y.txt", true)]
    [InlineData("file:///*", "https://example.com/", false)]
    [InlineData("http://127.0.0.1/*", "http://127.0.0.1/a", true)]
    public void MatchesByChromesRules(string pattern, string url, bool matches) => Assert.Equal(matches, P(pattern).Matches(new Uri(url)));

    [Fact] public void ARelativeAddressMatchesNothing() => Assert.False(P("<all_urls>").Matches(new Uri("/x", UriKind.Relative)));

    [Theory]
    [InlineData("<all_urls>", "example.com", true)]
    [InlineData("*://*/*", "anything.example", true)]
    [InlineData("https://*/*", "example.com", true)]
    [InlineData("https://*.google.com/*", "google.com", true)]
    [InlineData("https://*.google.com/*", "mail.google.com", true)]
    [InlineData("https://*.mail.google.com/*", "google.com", true)]
    [InlineData("https://*.google.com/*", "example.com", false)]
    [InlineData("https://*.google.com/*", "oogle.com", false)]
    [InlineData("https://mail.google.com/*", "google.com", true)]
    [InlineData("https://mail.google.com/*", "mail.google.com", true)]
    [InlineData("https://mail.google.com/*", "docs.google.com", false)]
    [InlineData("https://google.com/*", "mail.google.com", false)]
    [InlineData("https://example.com/*", "EXAMPLE.com.", true)]
    [InlineData("file:///*", "example.com", false)]
    [InlineData("chrome-extension://*/*", "example.com", false)]
    [InlineData("<all_urls>", "", false)]
    public void CoversAHostOrASiteUnderIt(string pattern, string host, bool covers) => Assert.Equal(covers, P(pattern).CoversHost(host));

    [Theory]
    [InlineData("<all_urls>", true)]
    [InlineData("*://*/*", true)]
    [InlineData("https://*/*", true)]
    [InlineData("https://*:8080/*", true)]
    [InlineData("https://*.example.com/*", false)]
    [InlineData("https://example.com/*", false)]
    [InlineData("file:///*", false)]
    [InlineData("chrome-extension://*/*", false)]
    public void AllHostsIsEveryWebsite(string pattern, bool all) => Assert.Equal(all, P(pattern).AllHosts);

    [Fact] public void PatternsAreEqualByWhatTheySay() => Assert.Equal(P("https://a.com/*"), P("https://a.com/*"));

    [Theory]
    [InlineData("*", "", true)]
    [InlineData("*", "/a/b?c", true)]
    [InlineData("/a*b", "/a/x/b", true)]
    [InlineData("/a*b", "/a/x/c", false)]
    [InlineData("/a**", "/a", true)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/*.js", "/x.json", false)]
    public void GlobsHaveOnlyStars(string pattern, string text, bool matches) => Assert.Equal(matches, MatchPattern.Glob(pattern, text));
}
