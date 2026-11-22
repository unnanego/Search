using System.Text.Json.Nodes;
using Search.Engine;

namespace Search.Engine.Tests;

/// Shield's rules and sheets as the extension is written from them, and the page scripts that go with them.
public class ShieldTests
{
    private static JsonObject[] Rules() => Shield.Rules(new string[0]).Select(r => r!.AsObject()).ToArray();

    [Fact]
    public void AdStatusIsLetThroughOnYouTubeOnlyAboveTheThirdPartyBlock()
    {
        var allow = Assert.Single(Rules(), r => r["action"]!["type"]!.GetValue<string>() == "allow");
        Assert.Equal("||static.doubleclick.net/instream/ad_status.js", allow["condition"]!["urlFilter"]!.GetValue<string>());
        Assert.Equal(new[] { "youtube.com", "youtube-nocookie.com" }, allow["condition"]!["initiatorDomains"]!.AsArray().Select(d => d!.GetValue<string>()));
        var thirdParty = Assert.Single(Rules(), r => r["condition"]!["domainType"]?.GetValue<string>() == "thirdParty");
        Assert.Contains("doubleclick.net", thirdParty["condition"]!["requestDomains"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.True(allow["priority"]!.GetValue<int>() > thirdParty["priority"]!.GetValue<int>());
    }

    [Fact]
    public void APausedSiteStillOutranksEveryRule()
    {
        var rules = Shield.Rules(new[] { "youtube.com" }).Select(r => r!.AsObject()).ToArray();
        var paused = Assert.Single(rules, r => r["action"]!["type"]!.GetValue<string>() == "allowAllRequests");
        Assert.All(rules.Where(r => r != paused), r => Assert.True(r["priority"]!.GetValue<int>() < paused["priority"]!.GetValue<int>()));
    }

    [Fact]
    public void RuleIdsAreUnique()
    {
        var ids = Rules().Select(r => r["id"]!.GetValue<int>()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void RedditsPromotedPostsAreHiddenOnRedditOnly()
    {
        var (matches, css) = Assert.Single(Shield.BySite(), s => s.Css.Contains("shreddit-ad-post"));
        Assert.Equal(new[] { "*://reddit.com/*", "*://*.reddit.com/*" }, matches);
        foreach (var selector in new[] { "shreddit-ad-post", "shreddit-comments-page-ad", "shreddit-sidebar-ad", "shreddit-dynamic-ad-link", "comment-tree-ad", ".promotedlink" }) Assert.Contains(selector, css);
        Assert.DoesNotContain("shreddit-post,", css);
        Assert.EndsWith(" { display: none !important; }", css);
    }

    [Fact]
    public void YouTubePageScriptAnswersTheBlockerTestsAndHoldsTheFirstVideo()
    {
        var script = Shield.YouTubePage(new string[0]);
        Assert.Contains(@"googleads\.g\.doubleclick\.net\/pagead\/id", script);
        Assert.Contains("navigator.sendBeacon", script);
        Assert.Contains("bkaEnforcementMessageViewModel", script);
        Assert.Contains("loadVideoById", script);
        // Single backslashes in the file: the Swift source's doubled ones belong to its string literal.
        Assert.Contains(@"/^\d+$/", script);
    }

    [Fact]
    public void SitesSheetsAreAdoptedNotStyleElementsFirst()
    {
        var sheet = Scripts.Read("sheet.js");
        Assert.Contains("new CSSStyleSheet()", sheet);
        Assert.Contains("document.adoptedStyleSheets", sheet);
        foreach (var name in new[] { "dress.js", "curtain.js" })
        {
            var script = Scripts.Read(name);
            Assert.Contains("__searchSheet('office-veil'", script);
            Assert.DoesNotContain("createElement('style')", script);
        }
    }
}
