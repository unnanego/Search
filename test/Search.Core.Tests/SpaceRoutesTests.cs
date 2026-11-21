using Search.Core;

namespace Search.Core.Tests;

public class SpaceRoutesTests
{
    private const string Work = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Mail = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Club = "1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB";
    private const string Gone = "C9A646D3-9C61-4CB7-BFCD-EE2522C8F633";

    public SpaceRoutesTests() => TempStore.Ready();

    private static Guid G(string text) => Guid.Parse(text);
    private static Uri U(string text) => new(text);

    private static (Spaces Spaces, SpaceRoutes Routes, Dictionary<string, string> Settings) Fresh(Dictionary<string, string>? settings = null)
    {
        var file = TempStore.Unique("spaces");
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File(file), $$"""[{"id":"00000000-0000-0000-0000-000000000001","name":"Personal","colour":0},{"id":"{{Work}}","name":"Work","colour":1},{"id":"{{Mail}}","name":"Mail","colour":0,"profile":"{{Club}}"}]""");
        settings ??= [];
        var spaces = new Spaces(file, settings);
        return (spaces, new SpaceRoutes(spaces), settings);
    }

    [Theory]
    [InlineData("https://www.Notion.so/x", "notion.so")]
    [InlineData("notion.so", "notion.so")]
    [InlineData("*.notion.so", "notion.so")]
    [InlineData("  Mail.Google.com/inbox  ", "mail.google.com")]
    [InlineData("notion.so.", "notion.so")]
    [InlineData("notion.so:8080", "notion.so")]
    [InlineData("www.com", "www.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("[::1]", "::1")]
    [InlineData("intranet", null)]
    [InlineData("exa_mple.com", null)]
    [InlineData("a..b", null)]
    [InlineData("", null)]
    [InlineData("*.", null)]
    [InlineData("two words.com", null)]
    public void WhatIsTypedIsASite(string typed, string? site) => Assert.Equal(site, SpaceRoutes.Site(typed));

    [Fact]
    public void OnlyWebAddressesHaveASite()
    {
        Assert.Equal("example.com", SpaceRoutes.SiteOf(U("http://WWW.Example.com:8080/a")));
        Assert.Equal("::1", SpaceRoutes.SiteOf(U("http://[::1]:3000/")));
        Assert.Null(SpaceRoutes.SiteOf(U("ftp://example.com/file")));
        Assert.Null(SpaceRoutes.SiteOf(U("file:///C:/notes.txt")));
        Assert.Null(SpaceRoutes.SiteOf(U("about:blank")));
    }

    [Fact]
    public void TheMostSpecificSiteWins()
    {
        SpaceRoute[] routes = [new("notion.so", G(Work)), new("mail.notion.so", G(Mail)), new("so", G(Club))];
        Assert.Equal(G(Mail), SpaceRoutes.Match(U("https://mail.notion.so/inbox"), routes)?.Space);
        Assert.Equal(G(Mail), SpaceRoutes.Match(U("https://a.mail.notion.so/"), routes)?.Space);
        Assert.Equal(G(Work), SpaceRoutes.Match(U("https://www.notion.so/page"), routes)?.Space);
        Assert.Equal(G(Work), SpaceRoutes.Match(U("https://team.notion.so/"), routes)?.Space);
        Assert.Equal(G(Club), SpaceRoutes.Match(U("https://notnotion.so/"), routes)?.Space);
        Assert.Null(SpaceRoutes.Match(U("https://notion.com/"), routes));
        Assert.Null(SpaceRoutes.Match(U("mailto:a@notion.so"), routes));
        Assert.Null(SpaceRoutes.Match(U("https://[::1]/"), [new("1", G(Work))]));
    }

    [Fact]
    public void ARuleSendsOnlyWhileOnAndOnlyToASpaceStillThere()
    {
        var (spaces, routes, _) = Fresh();
        routes.Set("notion.so", G(Work));
        routes.Set("github.com", G(Gone));
        Assert.Null(routes.SpaceFor(U("https://notion.so/")));
        routes.On = true;
        Assert.Null(routes.SpaceFor(U("https://notion.so/")));
        spaces.UsesSpaces = true;
        Assert.Equal(G(Work), routes.SpaceFor(U("https://notion.so/"))?.Id);
        Assert.Null(routes.SpaceFor(U("https://github.com/")));
        Assert.True(routes.Gone(routes.Rules[1]));
        Assert.False(routes.Gone(routes.Rules[0]));
        spaces.Remove(G(Work));
        Assert.Null(routes.SpaceFor(U("https://notion.so/")));
    }

    [Fact]
    public void RulesAreKeptInTheSettingsAsTheMacKeepsThem()
    {
        var (spaces, routes, settings) = Fresh();
        var said = new List<string?>();
        routes.PropertyChanged += (_, e) => said.Add(e.PropertyName);
        routes.On = true;
        routes.Set("notion.so", G(Work));
        routes.Set("mail.google.com", G(Mail));
        routes.Set("notion.so", G(Mail));
        routes.Set("notion.so", G(Mail));
        Assert.Equal([new SpaceRoute("notion.so", G(Mail)), new SpaceRoute("mail.google.com", G(Mail))], routes.Rules);
        Assert.Equal($$"""[{"site":"notion.so","space":"{{Mail}}"},{"site":"mail.google.com","space":"{{Mail}}"}]""", settings[SpaceRoutes.RulesKey]);
        Assert.Equal("true", settings[SpaceRoutes.Key]);
        routes.Remove("notion.so");
        routes.Remove("nothing.com");
        Assert.Equal([nameof(SpaceRoutes.On), nameof(SpaceRoutes.Rules), nameof(SpaceRoutes.Rules), nameof(SpaceRoutes.Rules), nameof(SpaceRoutes.Rules)], said);
        var again = new SpaceRoutes(spaces, settings);
        Assert.True(again.On);
        Assert.Equal([new SpaceRoute("mail.google.com", G(Mail))], again.Rules);
    }

    [Fact]
    public void RulesThatDontReadArePassedOver()
    {
        var text = $$"""[{"site":"https://www.Notion.so","space":"{{Work}}"},{"site":"intranet","space":"{{Work}}"},{"site":"github.com","space":"nope"},{"site":"gitlab.com","space":"{{Work}}","order":1},"x",{"space":"{{Work}}"}]""";
        Assert.Equal([new SpaceRoute("notion.so", G(Work))], SpaceRoutes.Decode(text));
        Assert.Empty(SpaceRoutes.Decode("{\"site\":\"notion.so\"}"));
        Assert.Empty(SpaceRoutes.Decode("[{"));
        Assert.Empty(SpaceRoutes.Decode(null));
    }

    [Fact]
    public void TheChoicesAreEverySpace()
    {
        var (_, routes, _) = Fresh();
        Assert.Equal(["Personal", "Work", "Mail"], routes.Choices().Select(c => c.Name));
        Assert.Equal(["Personal › Personal", "Personal › Work", "Club › Mail"], routes.Choices(p => p == G(Club) ? "Club" : "Personal").Select(c => c.Name));
        Assert.Equal(G(Work), routes.Choices()[1].Id);
    }
}
