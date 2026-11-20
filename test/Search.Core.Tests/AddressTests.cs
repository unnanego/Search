using Search.Core;

namespace Search.Core.Tests;

public class AddressTests
{
    private static string? Typed(string text) => Address.UrlFrom(text)?.AbsoluteUri;

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("Example.COM/Path?Q=1#top", "https://example.com/Path?Q=1#top")]
    [InlineData("10.io", "https://10.io/")]
    [InlineData("10.org/about", "https://10.org/about")]
    [InlineData("10.0.0.1.example.com", "https://10.0.0.1.example.com/")]
    [InlineData("192.168.1.1.example.com", "https://192.168.1.1.example.com/")]
    [InlineData("bücher.de", "https://bücher.de/")]
    [InlineData("  github.com/team  ", "https://github.com/team")]
    public void HostsBecomeHttps(string typed, string wanted) => Assert.Equal(wanted, Typed(typed));

    [Theory]
    [InlineData("hello world")]
    [InlineData("todo")]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("me@example.com")]
    [InlineData("mailto:me@example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("-bad.com")]
    [InlineData("a..com")]
    [InlineData("example.c")]
    [InlineData("")]
    [InlineData("/etc/hosts")]
    public void WordsAreNoPlace(string typed) => Assert.Null(Typed(typed));

    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("app.localhost:5173/x", "http://app.localhost:5173/x")]
    [InlineData("printer.local", "http://printer.local/")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("0.0.0.0:8080", "http://0.0.0.0:8080/")]
    [InlineData("[::1]:3000", "http://[::1]:3000/")]
    public void LocalServersGetPlainHttp(string typed, string wanted) => Assert.Equal(wanted, Typed(typed));

    [Theory]
    [InlineData("10.0.0.1", "http://10.0.0.1/")]
    [InlineData("10.255.255.254:9000", "http://10.255.255.254:9000/")]
    [InlineData("192.168.1.1", "http://192.168.1.1/")]
    [InlineData("172.16.0.1", "http://172.16.0.1/")]
    [InlineData("172.31.255.254", "http://172.31.255.254/")]
    [InlineData("172.15.0.1", "https://172.15.0.1/")]
    [InlineData("172.32.0.1", "https://172.32.0.1/")]
    [InlineData("8.8.8.8", "https://8.8.8.8/")]
    public void PrivateRangesAreLocal(string typed, string wanted) => Assert.Equal(wanted, Typed(typed));

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTP://Example.com/A", "http://example.com/A")]
    [InlineData("search://settings", "search://settings/")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("data:text/plain,hi", "data:text/plain,hi")]
    public void SchemesWeShowAreTakenAtTheirWord(string typed, string wanted) => Assert.Equal(wanted, Typed(typed));

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://www.example.com/a/b?c=d#e", "www.example.com/a/b?c=d#e")]
    [InlineData("https://example.com:8443/", "example.com:8443")]
    [InlineData("https://example.com/?", "example.com/?")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("http://localhost:3000/", "localhost:3000")]
    [InlineData("http://localhost:3000/app", "localhost:3000/app")]
    [InlineData("https://localhost/", "https://localhost/")]
    [InlineData("http://192.168.1.1/", "192.168.1.1")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("search://settings/", "search://settings/")]
    [InlineData("file:///C:/a%20b/c.txt", "file:///C:/a%20b/c.txt")]
    public void EditableIsShortestThatGoesBack(string page, string wanted) => Assert.Equal(wanted, Address.Editable(new Uri(page)));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://Example.com/A/%7Euser?Q=One#Frag")]
    [InlineData("https://example.com/a%20b/c%2Fd")]
    [InlineData("http://example.com/x")]
    [InlineData("http://[::1]:3000/")]
    [InlineData("https://bücher.de/straße")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("data:text/html,<b>hi</b>")]
    public void EditableRoundTrips(string page)
    {
        var url = new Uri(page);
        Assert.Equal(url.AbsoluteUri, Address.UrlFrom(Address.Editable(url))?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.example.com/a/b?x=1", "example.com/a/b")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("about:blank", "about:blank")]
    public void PrettyDropsWhatNobodyReads(string page, string wanted) => Assert.Equal(wanted, Address.Pretty(new Uri(page)));

    [Fact]
    public void WithoutWwwDropsOnlyALeadingOne()
    {
        Assert.Equal("example.com", Address.WithoutWww("WWW.example.com"));
        Assert.Equal("wwwexample.com", Address.WithoutWww("wwwexample.com"));
    }

    [Fact]
    public void FileOpensOnlyExistingFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"search-address-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a b.txt");
        File.WriteAllText(file, "hi");
        try
        {
            Assert.Equal(new Uri(file), Address.File(file));
            Assert.Equal(new Uri(file), Address.File(file.Replace('\\', '/')));
            Assert.True(Address.File("  " + file + "  ")?.IsFile);
            Assert.Null(Address.File(folder));
            Assert.Null(Address.File(Path.Combine(folder, "missing.txt")));
            Assert.Null(Address.File(@"~\" + Guid.NewGuid().ToString("N")));
            Assert.Null(Address.File(@"\\server\share\x.txt"));
            Assert.Null(Address.File("example.com"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
