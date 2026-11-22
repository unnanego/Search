using System.Text;
using System.Text.Json;
using Search.Core;

namespace Search.Core.Tests;

public class ImportArcTests
{
    /// Two spaces, "Work" in the default profile and "Raycast" in "Profile 1"; Work's pinned list a page and a
    /// folder, its Today a page that stays behind; favourites for the default profile.
    internal const string Sidebar = """
        {"sidebar":{"containers":[{"global":{}},{
          "spaces":["s1",{"id":"s1","title":"Work","profile":{"default":{}},"customInfo":{"iconType":{"emoji_v2":"💼"}},
                          "newContainerIDs":[{"unpinned":{}},"today",{"pinned":{}},"pinned"]},
                    "s2",{"id":"s2","title":" Raycast ","profile":{"custom":{"_0":{"directoryBasename":"Profile 1"}}},"customInfo":{"iconType":{"icon":"bulb"}},
                          "containerIDs":["pinned","p2"]}],
          "items":["pinned",{"id":"pinned","childrenIds":["t1","f1","t1"],"data":{"itemContainer":{}}},
                   {"id":"t1","title":null,"data":{"tab":{"savedURL":"https://github.com/","savedTitle":"GitHub"}}},
                   {"id":"f1","title":"Docs","childrenIds":["t2","t3","empty"],"data":{"list":{}}},
                   {"id":"t2","title":"Spec","data":{"tab":{"savedURL":"https://me:pw@docs.example/spec","savedTitle":"ignored"}}},
                   {"id":"t3","data":{"tab":{"savedURL":"javascript:alert(1)"}}},
                   {"id":"empty","title":"Nothing","childrenIds":[],"data":{"list":{}}},
                   {"id":"today","childrenIds":["t4"],"data":{}},
                   {"id":"t4","data":{"tab":{"savedURL":"https://today.example/"}}},
                   {"id":"p2","childrenIds":["t5"]},
                   {"id":"t5","data":{"tab":{"savedURL":"https://raycast.com/","savedTitle":"Raycast"}}},
                   {"id":"fav","childrenIds":["t6"]},
                   {"id":"t6","title":"Mail","data":{"tab":{"savedURL":"https://mail.google.com/"}}}],
          "topAppsContainerIDs":[{"default":{}},"fav"]}]}}
        """;

    [Fact]
    public void ReadsSpacesPinsFoldersAndFavourites()
    {
        var sidebar = Arc.Parse(Encoding.UTF8.GetBytes(Sidebar), null)!;
        Assert.Equal(["Work", "Raycast"], sidebar.Spaces.Select(s => s.Name));
        var work = sidebar.Spaces[0];
        Assert.Equal(("Default", "💼", "briefcase"), (work.Profile, work.Emoji, work.Symbol));
        var page = Assert.IsType<ArcNode.Page>(work.Pinned[0]);
        Assert.Equal(("GitHub", "https://github.com/"), (page.Item.Title, page.Item.Url.AbsoluteUri));
        var folder = Assert.IsType<ArcNode.Folder>(work.Pinned[1]);
        Assert.Equal("Docs", folder.Title);
        Assert.Equal("https://docs.example/spec", Assert.IsType<ArcNode.Page>(Assert.Single(folder.Items)).Item.Url.AbsoluteUri);
        // Each item once: the second "t1" isn't a second pin.
        Assert.Equal(2, work.Pinned.Count);
        Assert.Equal(("Profile 1", "lightbulb"), (sidebar.Spaces[1].Profile, sidebar.Spaces[1].Symbol));
        Assert.Equal(["Mail"], sidebar.Favorites.Select(f => f.Title));
        Assert.Equal(["Default"], sidebar.FavoritesByProfile.Keys);
        Assert.Equal(3, sidebar.PinnedCount);
    }

    [Fact]
    public void OneProfileAtATime()
    {
        var sidebar = Arc.Parse(Encoding.UTF8.GetBytes(Sidebar), "Profile 1")!;
        Assert.Equal(["Raycast"], sidebar.Spaces.Select(s => s.Name));
        Assert.Empty(sidebar.Favorites);
    }

    [Fact]
    public void ReadsTheFileBesideArcsUserData()
    {
        using var at = new ImportFixture();
        at.Write(Path.Combine(at.ArcFolder, "User Data", "Default", "Bookmarks"), """{"roots":{}}""");
        at.Write(Path.Combine(at.ArcFolder, "StorableSidebar.json"), Sidebar);
        var arc = ImportSource.Installed(at.Roots).Single();
        Assert.Equal((2, 4), arc.SidebarCounts());
        Assert.Null(ImportSource.Known(at.Roots).First(s => s.Name == "Chrome").Sidebar());
    }

    [Theory]
    [InlineData("💼", null, "Anything", "briefcase")]
    [InlineData("❤️", null, "Anything", "heart")]
    [InlineData(null, "construction", "Anything", "chevron.left.forwardslash.chevron.right")]
    [InlineData(null, null, "My AI tools", "sparkles")]
    [InlineData(null, null, "Maison", "house")]
    [InlineData(null, null, "Dev stuff", "chevron.left.forwardslash.chevron.right")]
    [InlineData(null, null, "Developer", "briefcase")]
    [InlineData(null, null, "Studies", "graduationcap")]
    [InlineData(null, null, "Zzz", "briefcase")]
    public void ASpacesPictureIsTheNearestOfSearchs(string? emoji, string? icon, string name, string symbol) => Assert.Equal(symbol, Arc.Symbol(emoji, icon, null, name));

    [Fact]
    public void ZensPicturesHaveTheirOwnTable() => Assert.Equal("gamecontroller", Arc.Symbol(null, null, "game-controller", "x"));

    [Fact]
    public void NotJsonIsNoSidebar() => Assert.Null(Arc.Parse("nope"u8.ToArray(), null));
}

public class ImportZenTests
{
    private const string Sessions = """
        {"spaces":[
           {"uuid":"{s1}","name":"Home","icon":"🏠","theme":{"gradientColors":[{"c":"#34A853"},{"c":[66,133,244],"isPrimary":true}],"rotation":90,"texture":0.5}},
           {"uuid":"{s2}","name":"Code","icon":"chrome://browser/skin/zen-icons/selectable/code.svg"}],
         "folders":[{"id":"f1","name":"Reading"},{"id":"f2","name":"Inner","parentId":"f1"},{"id":"live","name":"Feed","isLiveFolder":true},{"id":"split","splitViewGroup":true,"parentId":"f1"}],
         "tabs":[
           {"pinned":true,"zenWorkspace":"{s1}","entries":[{"url":"https://a.example/","title":"A"}],"index":1},
           {"pinned":true,"zenWorkspace":"{s1}","groupId":"f2","entries":[{"url":"https://b.example/","title":"B"}]},
           {"pinned":true,"zenWorkspace":"{s1}","groupId":"split","entries":[{"url":"https://c.example/","title":"C"}]},
           {"pinned":true,"groupId":"live","entries":[{"url":"https://feed.example/"}]},
           {"zenEssential":true,"entries":[{"url":"https://mail.example/","title":"Mail"}]},
           {"pinned":false,"entries":[{"url":"https://loose.example/"}]},
           {"pinned":true,"zenWorkspace":"{s2}","zenStaticLabel":" Repo ","entries":[{"url":"https://later.example/"}],"_zenPinnedInitialState":{"entry":{"url":"https://first.example/","title":"First"}}},
           {"pinned":true,"zenWorkspace":"{gone}","entries":[{"url":"https://orphan.example/"}]}]}
        """;

    [Fact]
    public void ReadsSpacesWithTheirThemesFoldersAndEssentials()
    {
        using var doc = JsonDocument.Parse(Sessions);
        var sidebar = Zen.Parse(doc.RootElement)!;
        Assert.Equal(["Home", "Code"], sidebar.Spaces.Select(s => s.Name));
        var home = sidebar.Spaces[0];
        Assert.Equal(("🏠", "house"), (home.Emoji, home.Symbol));
        // The primary dot leads: blue.
        Assert.Equal(1, home.Colour);
        Assert.Equal(["#4285F4", "#34A853"], home.Gradient!.Colours);
        Assert.Equal((90, true), (home.Gradient.Angle, home.Gradient.Grain));
        Assert.Equal(["A", "Reading", "orphan.example"], home.Pinned.Select(Title));
        var reading = Assert.IsType<ArcNode.Folder>(home.Pinned[1]);
        // A split view inside a folder is the folder's.
        Assert.Equal(["Inner", "C"], reading.Items.Select(Title));
        Assert.Equal(["B"], Assert.IsType<ArcNode.Folder>(reading.Items[0]).Items.Select(Title));
        var code = sidebar.Spaces[1];
        Assert.Equal("chevron.left.forwardslash.chevron.right", code.Symbol);
        var pinned = Assert.IsType<ArcNode.Page>(Assert.Single(code.Pinned));
        Assert.Equal(("Repo", "https://first.example/"), (pinned.Item.Title, pinned.Item.Url.AbsoluteUri));
        Assert.Equal(["Mail"], sidebar.Favorites.Select(f => f.Title));
    }

    private static string Title(ArcNode node) => node switch { ArcNode.Page p => p.Item.Title, ArcNode.Folder f => f.Title, _ => "" };

    [Fact]
    public void ReadsZensSessionFileThroughGeckoSource()
    {
        using var at = new ImportFixture();
        var profile = at.Path("Roaming", "zen", "Profiles", "z.Default (release)");
        ImportFixture.Db(Path.Combine(profile, "places.sqlite"), "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT)");
        at.Write(Path.Combine(profile, "zen-sessions.jsonlz4"), ImportFixture.JsonLz4(Sessions));
        var zen = ImportSource.Installed(at.Roots).Single();
        Assert.True(zen.KeepsSpaces);
        Assert.Equal((2, 6), zen.SidebarCounts());
    }

    [Fact]
    public void ATornFileFallsBackToItsNewestBackup()
    {
        using var at = new ImportFixture();
        var profile = at.Path("Roaming", "zen", "Profiles", "z");
        at.Write(Path.Combine(profile, "zen-sessions.jsonlz4"), "mozLz40\0garbage"u8.ToArray());
        at.Write(Path.Combine(profile, "zen-sessions-backup", "zen-sessions-2025-12-01.jsonlz4"), ImportFixture.JsonLz4("""{"spaces":[{"uuid":"a","name":"Older"}]}"""));
        at.Write(Path.Combine(profile, "zen-sessions-backup", "zen-sessions-2025-12-02.jsonlz4"), ImportFixture.JsonLz4("""{"spaces":[{"uuid":"a","name":"Newer"}]}"""));
        Assert.Equal(["Newer"], Zen.InProfile(profile)!.Spaces.Select(s => s.Name));
    }

    [Fact]
    public void BeforeWindowSyncTheyWereTablesInPlaces()
    {
        using var at = new ImportFixture();
        var profile = at.Path("Roaming", "zen", "Profiles", "z");
        var places = ImportFixture.Db(Path.Combine(profile, "places.sqlite"),
            "CREATE TABLE zen_workspaces (id INTEGER PRIMARY KEY, uuid TEXT, name TEXT, icon TEXT, position INTEGER, created_at INTEGER, theme_colors TEXT, theme_rotation INTEGER, theme_texture REAL)",
            "CREATE TABLE zen_pins (id INTEGER PRIMARY KEY, uuid TEXT, title TEXT, url TEXT, workspace_uuid TEXT, position INTEGER, is_essential INTEGER, is_group INTEGER, folder_parent_uuid TEXT)",
            "INSERT INTO zen_workspaces VALUES (1, 'w2', 'Second', NULL, 2, 0, NULL, NULL, NULL)",
            """INSERT INTO zen_workspaces VALUES (2, 'w1', 'First', '🎮', 1, 0, '[{"c":[230,60,60]},{"c":"rgb(255, 200, 0)"}]', 45, 0.0)""",
            "INSERT INTO zen_pins VALUES (1, 'g1', 'Games', NULL, 'w1', 0, 0, 1, NULL)",
            "INSERT INTO zen_pins VALUES (2, 'p1', 'Steam', 'https://store.steampowered.com/', 'w1', 1, 0, 0, 'g1')",
            "INSERT INTO zen_pins VALUES (3, 'p2', NULL, 'https://second.example/', 'w2', 2, 0, 0, NULL)",
            "INSERT INTO zen_pins VALUES (4, 'p3', 'Mail', 'https://mail.example/', NULL, 3, 1, 0, NULL)");
        var sidebar = Zen.FromPlaces(places, profile)!;
        Assert.Equal(["First", "Second"], sidebar.Spaces.Select(s => s.Name));
        var first = sidebar.Spaces[0];
        Assert.Equal(("gamecontroller", 4), (first.Symbol, first.Colour));
        Assert.Equal((45, false), (first.Gradient!.Angle, first.Gradient.Grain));
        Assert.Equal("Steam", Assert.IsType<ArcNode.Page>(Assert.IsType<ArcNode.Folder>(Assert.Single(first.Pinned)).Items.Single()).Item.Title);
        Assert.Equal("second.example", Assert.IsType<ArcNode.Page>(sidebar.Spaces[1].Pinned.Single()).Item.Title);
        Assert.Equal(["Mail"], sidebar.Favorites.Select(f => f.Title));
    }

    [Fact]
    public void TheOldestKeptThemInTheSessionStoreAndAFileBeside()
    {
        using var at = new ImportFixture();
        var profile = at.Path("Roaming", "zen", "Profiles", "z");
        at.Write(Path.Combine(profile, "sessionstore.jsonlz4"), ImportFixture.JsonLz4("""{"windows":[{"tabs":[{"pinned":true,"attributes":{"zen-workspace-id":"w"},"entries":[{"url":"https://old.example/","title":"Old"}]}]}]}"""));
        at.Write(Path.Combine(profile, "zen-workspaces.json"), """{"workspaces":[{"uuid":"w","name":"Ancient"}]}""");
        var sidebar = Zen.InProfile(profile)!;
        Assert.Equal("Ancient", sidebar.Spaces.Single().Name);
        Assert.Equal("Old", Assert.IsType<ArcNode.Page>(sidebar.Spaces.Single().Pinned.Single()).Item.Title);
    }

    [Theory]
    [InlineData(0.26, 0.52, 0.96, 1)]
    [InlineData(0.2, 0.66, 0.45, 2)]
    [InlineData(0.96, 0.62, 0.2, 3)]
    [InlineData(0.9, 0.2, 0.25, 4)]
    [InlineData(0.62, 0.4, 0.9, 5)]
    [InlineData(0.5, 0.5, 0.52, 0)]
    public void AColourIsTheNearestSpaceColourByHue(double r, double g, double b, int index) => Assert.Equal(index, Zen.Nearest((r, g, b)));

    [Fact]
    public void ColoursReadEveryWayZenWritesThem()
    {
        using var doc = JsonDocument.Parse("""[[255,0,0],"#00FF00","rgb(0, 0, 255)",[300,0,0],"blue"]""");
        var read = doc.RootElement.EnumerateArray().Select(Zen.Rgb).ToList();
        Assert.Equal([(1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)], read.Take(3).Select(c => c!.Value));
        Assert.Null(read[3]);
        Assert.Null(read[4]);
    }
}
