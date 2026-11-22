using Search.Engine;

namespace Search.Engine.Tests;

/// The scripts Search puts in extensions' pages and workers, as they're filled in for a page.
public class ExtensionScriptsTests
{
    [Fact]
    public void PageShimFillsEveryBlank()
    {
        var shim = ExtensionHost.PageShim("popup", 1234, new long[] { 7, 8 });
        Assert.DoesNotContain("__SEARCH_", shim);
        Assert.Contains("const mode = \"popup\";", shim);
        Assert.Contains("windowId: 1234", shim);
        Assert.Contains("new Set([7,8])", shim);
    }

    [Fact]
    public void PageShimWithNoWindowInFrontSaysNull() => Assert.Contains("windowId: null", ExtensionHost.PageShim("panel", null, new long[0]));

    [Fact]
    public void WorkerShimHasNoBlanks() => Assert.DoesNotContain("__SEARCH_", Scripts.Read("extension-worker.js"));

    [Fact]
    public void BridgeIsInSearchsOwnFolderNotAReservedName()
    {
        // Chromium refuses any file or folder an extension ships whose name starts with "_".
        var url = ExtensionHost.BridgeUrl("abcdefghijklmnopabcdefghijklmnop");
        Assert.Equal("chrome-extension://abcdefghijklmnopabcdefghijklmnop/search-browser/bridge.html", url);
        Assert.DoesNotContain("/_", url);
    }

    [Theory]
    [InlineData("popup.html", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html")]
    [InlineData("/ui/panel.html", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/ui/panel.html")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/x.html", "chrome-extension://abcdefghijklmnopabcdefghijklmnop/x.html")]
    public void PageUrlIsTheExtensionsOwn(string path, string url) => Assert.Equal(url, ExtensionHost.PageUrl("abcdefghijklmnopabcdefghijklmnop", path));

    [Theory]
    [InlineData("store-page.js")]
    [InlineData("store-relay.js")]
    public void StoreScriptsAskForTheirHosts(string name) => Assert.Contains("__SEARCH_STORE_HOSTS__", Scripts.Read(name));
}
