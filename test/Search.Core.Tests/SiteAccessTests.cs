using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Core.Tests;

public class SiteAccessTests
{
    private const string Id = "nkbihfbeogaeaoehlefnkodbefgpgknn";
    private static readonly SiteAccess Some = new(SiteMode.Some, ["example.com"]);

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();
    private static string Compact(JsonNode node) => node.ToJsonString();
    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    // Hosts.

    [Theory]
    [InlineData("Example.com", "example.com")]
    [InlineData("  example.com \n", "example.com")]
    [InlineData("https://www.example.com:8080/path?q", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("https://*.example.com/", "example.com")]
    [InlineData("news.example.co.uk", "news.example.co.uk")]
    [InlineData("www.com", "www.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://", null)]
    [InlineData("exa mple.com", null)]
    [InlineData("[::1]", null)]
    public void AHostIsReadFromWhatWasTyped(string typed, string? host) => Assert.Equal(host, SiteAccess.Host(typed));

    [Fact] public void UniqueKeepsTheFirstOfEach() => Assert.Equal(["b.com", "a.com"], SiteAccess.Unique(["b.com", "a.com", "b.com"]));

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("news.example.com", true)]
    [InlineData("NEWS.Example.com", true)]
    [InlineData("notexample.com", false)]
    [InlineData("example.com.evil", false)]
    [InlineData("com", false)]
    public void ANamedSiteCoversItsSubdomains(string host, bool covered) => Assert.Equal(covered, SiteAccess.Covers(["example.com"], host));

    [Fact]
    public void EachModeCoversWhatItSays()
    {
        Assert.True(new SiteAccess().Covers("anything.example"));
        Assert.True(Some.Covers("a.example.com"));
        Assert.False(Some.Covers("other.com"));
        Assert.False((Some with { Mode = SiteMode.Click }).Covers("example.com"));
    }

    [Fact]
    public void OnlyAWebPageHasASite()
    {
        Assert.Equal("example.com", SiteAccess.Site(new Uri("https://Example.com/x")));
        Assert.Null(SiteAccess.Site(new Uri("file:///C:/x")));
        Assert.Null(SiteAccess.Site(null));
    }

    [Fact]
    public void AddingASiteMeansSomeSites()
    {
        var added = new SiteAccess(SiteMode.Click, ["a.com"]).Adding("b.com").Adding("a.com");
        Assert.Equal(new SiteAccess(SiteMode.Some, ["a.com", "b.com"]), added);
        Assert.True(added.Restricted);
    }

    [Fact]
    public void RemovingASiteTakesWhatIsUnderAndOverIt() =>
        Assert.Equal(["other.com"], new SiteAccess(SiteMode.Some, ["example.com", "news.example.com", "other.com"]).Removing("example.com").Hosts);

    [Fact] public void TheDefaultIsAllSitesUnrestricted() => Assert.Equal((SiteMode.All, false, 0), (new SiteAccess().Mode, new SiteAccess().Restricted, new SiteAccess().Hosts.Count));

    [Fact]
    public void ModesHaveTitles() => Assert.Equal(["All sites", "Some sites", "On click"], Enum.GetValues<SiteMode>().Select(SiteAccess.Title));

    [Fact]
    public void AccessIsEqualByModeAndHosts()
    {
        Assert.Equal(new SiteAccess(SiteMode.Some, ["a.com"]), new SiteAccess(SiteMode.Some, new List<string> { "a.com" }));
        Assert.Equal(new SiteAccess(SiteMode.Some, ["a.com"]).GetHashCode(), new SiteAccess(SiteMode.Some, ["a.com"]).GetHashCode());
        Assert.NotEqual(new SiteAccess(SiteMode.Some, ["a.com"]), new SiteAccess(SiteMode.Some, ["b.com"]));
    }

    // Kept.

    [Fact]
    public void KeptAsAJsonObjectUnderItsKey()
    {
        var notes = new Dictionary<string, string>();
        new SiteAccess(SiteMode.Some, ["example.com", "news.site"]).Save(Id, notes);
        Assert.Equal("""{"mode":"some","hosts":["example.com","news.site"]}""", notes["extensions.sites." + Id]);
        Assert.Equal(new SiteAccess(SiteMode.Some, ["example.com", "news.site"]), SiteAccess.Of(Id, notes));
    }

    [Fact]
    public void TheDefaultIsntKept()
    {
        var notes = new Dictionary<string, string> { [SiteAccess.Key(Id)] = """{"mode":"click","hosts":[]}""" };
        Assert.Equal(SiteMode.Click, SiteAccess.Of(Id, notes).Mode);
        new SiteAccess().Save(Id, notes);
        Assert.Empty(notes);
    }

    [Fact]
    public void HostsKeptWhileAnotherModeIsPicked()
    {
        var notes = new Dictionary<string, string>();
        new SiteAccess(SiteMode.All, ["a.com"]).Save(Id, notes);
        Assert.Equal(new SiteAccess(SiteMode.All, ["a.com"]), SiteAccess.Of(Id, notes));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"mode":"weird"}""")]
    [InlineData("""{"mode":3,"hosts":"a.com"}""")]
    public void WhatDoesntReadIsEverySite(string kept) => Assert.Equal(SiteMode.All, SiteAccess.Of(Id, new Dictionary<string, string> { [SiteAccess.Key(Id)] = kept }).Mode);

    [Fact]
    public void KeptHostsAreReadAsTyped() =>
        Assert.Equal(["example.com", "b.com"], SiteAccess.Of(Id, new Dictionary<string, string> { [SiteAccess.Key(Id)] = """{"mode":"some","hosts":["WWW.Example.com","example.com","b.com",3,"bad host"]}""" }).Hosts);

    [Fact]
    public void ForgottenWithTheExtension()
    {
        var notes = new Dictionary<string, string> { [SiteAccess.Key(Id)] = "{}", ["other"] = "x" };
        SiteAccess.Forget(Id, notes);
        Assert.Equal(["other"], notes.Keys);
    }

    [Fact] public void NothingKeptIsEverySite() => Assert.Equal(new SiteAccess(), SiteAccess.Of(Id, new Dictionary<string, string>()));

    // Patterns.

    [Theory]
    [InlineData("<all_urls>", "example.com", true, "*://*.example.com/*")]
    [InlineData("*://*/*", "example.com", true, "*://*.example.com/*")]
    [InlineData("*://*/*", "example.com", false, "*://example.com/*")]
    [InlineData("https://*/*", "localhost", true, "https://localhost/*")]
    [InlineData("https://*/*", "10.0.0.1", true, "https://10.0.0.1/*")]
    [InlineData("https://*.example.com/mail/*", "example.com", true, "https://*.example.com/mail/*")]
    [InlineData("https://*.example.com/*", "news.example.com", true, "https://*.news.example.com/*")]
    [InlineData("https://*.news.example.com/*", "example.com", true, "https://*.news.example.com/*")]
    [InlineData("https://*.news.example.com/*", "example.com", false, null)]
    [InlineData("https://mail.example.com/*", "example.com", true, "https://mail.example.com/*")]
    [InlineData("https://mail.example.com/*", "example.com", false, null)]
    [InlineData("https://example.com/*", "example.com", false, "https://example.com/*")]
    [InlineData("http://*:8080/*", "example.com", true, "http://*.example.com:8080/*")]
    [InlineData("https://other.com/*", "example.com", true, null)]
    [InlineData("file:///*", "example.com", true, null)]
    [InlineData("chrome-extension://*/*", "example.com", true, null)]
    public void APatternIsNarrowedToASite(string pattern, string host, bool subdomains, string? narrowed) =>
        Assert.Equal(narrowed, SiteAccess.Narrow(MatchPattern.Parse(pattern)!, host, subdomains));

    [Fact]
    public void ThePatternsForASiteAreEachOnceInOrder() =>
        Assert.Equal(["*://*.example.com/*", "https://mail.example.com/*"],
            SiteAccess.Patterns(new[] { "https://mail.example.com/*", "<all_urls>", "*://*/*", "file:///*", "https://other.com/*" }.Select(p => MatchPattern.Parse(p)!), "example.com", subdomains: true));

    [Theory]
    [InlineData("Bitwarden Password Manager", true)]
    [InlineData("1Password – Password Manager", true)]
    [InlineData("Proton Pass", true)]
    [InlineData("Dark Reader", false)]
    public void PasswordManagersNeedEverySite(string name, bool needs) => Assert.Equal(needs, SiteAccess.NeedsEverySite(name));

    [Fact]
    public void ExtensionsThatTurnOffPasswordSavingNeedEverySite()
    {
        Assert.True(SiteAccess.NeedsEverySite("Something", new Dictionary<string, object?> { ["privacy.services.passwordSavingEnabled"] = false }));
        Assert.False(SiteAccess.NeedsEverySite("Something", new Dictionary<string, object?> { ["privacy.services.passwordSavingEnabled"] = true }));
    }

    // Restricting the copy.

    private static readonly JsonObject Wide = Json("""
        {"manifest_version":3,"name":"Wide","permissions":["storage","tabs"],
         "host_permissions":["<all_urls>"],
         "optional_host_permissions":["https://*/*"],
         "content_scripts":[
           {"matches":["<all_urls>"],"exclude_matches":["https://example.com/private/*"],"js":["all.js"],"run_at":"document_start"},
           {"matches":["https://other.com/*"],"js":["other.js"]},
           {"matches":["https://*.example.com/mail/*","file:///*"],"js":["mail.js"]}]}
        """);

    [Fact]
    public void AllSitesLeavesTheManifestAsItIsInACopy()
    {
        var restricted = SiteAccess.Restrict(Wide, new SiteAccess());
        Assert.Equal(Compact(Wide), Compact(restricted));
        Assert.NotSame(Wide, restricted);
    }

    [Fact]
    public void SomeSitesNarrowsHostPermissions()
    {
        var restricted = SiteAccess.Restrict(Wide, new SiteAccess(SiteMode.Some, ["example.com", "news.site"]));
        Assert.Equal(["*://*.example.com/*", "*://*.news.site/*"], Strings(restricted["host_permissions"]));
        Assert.Equal(["storage", "tabs"], Strings(restricted["permissions"]));
        Assert.Equal(["https://*/*"], Strings(restricted["optional_host_permissions"]));
    }

    [Fact]
    public void SomeSitesNarrowsContentScriptsAndDropsTheOnesLeftWithNothing()
    {
        var scripts = SiteAccess.Restrict(Wide, Some)["content_scripts"]!.AsArray();
        Assert.Equal(2, scripts.Count);
        Assert.Equal(["*://*.example.com/*"], Strings(scripts[0]!["matches"]));
        Assert.Equal(["https://example.com/private/*"], Strings(scripts[0]!["exclude_matches"]));
        Assert.Equal(["all.js"], Strings(scripts[0]!["js"]));
        Assert.Equal("document_start", scripts[0]!["run_at"]!.GetValue<string>());
        Assert.Equal(["https://*.example.com/mail/*"], Strings(scripts[1]!["matches"]));
        Assert.Equal(["mail.js"], Strings(scripts[1]!["js"]));
    }

    [Fact]
    public void APatternThatDoesntReachTheSiteGoes()
    {
        var restricted = SiteAccess.Restrict(Json("""{"host_permissions":["https://other.com/*","*://*/*","https://mail.example.com/*"]}"""), Some);
        Assert.Equal(["*://*.example.com/*", "https://mail.example.com/*"], Strings(restricted["host_permissions"]));
    }

    [Fact]
    public void ManifestV2SitesAmongPermissionsAreNarrowedAndTheRestKept()
    {
        var restricted = SiteAccess.Restrict(Json("""{"manifest_version":2,"permissions":["tabs","<all_urls>","http://other.com/*","storage"]}"""), Some);
        Assert.Equal(["tabs", "*://*.example.com/*", "storage"], Strings(restricted["permissions"]));
    }

    [Fact]
    public void NoNamedSitesLeavesNoSites()
    {
        var restricted = SiteAccess.Restrict(Wide, new SiteAccess(SiteMode.Some));
        Assert.Empty(restricted["host_permissions"]!.AsArray());
        Assert.Empty(restricted["content_scripts"]!.AsArray());
    }

    [Fact]
    public void OnClickTakesAwayEverySiteAndGivesActiveTab()
    {
        var restricted = SiteAccess.Restrict(Wide, new SiteAccess(SiteMode.Click, ["example.com"]));
        Assert.Null(restricted["host_permissions"]);
        Assert.Null(restricted["content_scripts"]);
        Assert.Equal(["storage", "tabs", "activeTab"], Strings(restricted["permissions"]));
        Assert.Equal(["https://*/*"], Strings(restricted["optional_host_permissions"]));
    }

    [Fact]
    public void OnClickKeepsOneActiveTabAndManifestV2LosesItsSites()
    {
        Assert.Equal(["activeTab", "tabs"], Strings(SiteAccess.Restrict(Json("""{"permissions":["activeTab","<all_urls>","tabs"]}"""), new SiteAccess(SiteMode.Click))["permissions"]));
        Assert.Equal(["activeTab"], Strings(SiteAccess.Restrict(Json("""{"name":"x"}"""), new SiteAccess(SiteMode.Click))["permissions"]));
    }

    [Fact]
    public void TheOriginalIsntTouched()
    {
        var before = Compact(Wide);
        SiteAccess.Restrict(Wide, Some);
        SiteAccess.Restrict(Wide, new SiteAccess(SiteMode.Click));
        Assert.Equal(before, Compact(Wide));
    }
}
