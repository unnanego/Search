using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class ImportTakeTests
{
    public ImportTakeTests() => TempStore.Ready();

    private static Uri U(string text) => new(text);

    private static (ImportTarget Target, Spaces Spaces, Pins Pins, PinFolders Folders) Target(bool rows = false, bool folders = false)
    {
        var spaces = new Spaces(TempStore.Unique("spaces"), new Dictionary<string, string>());
        var pins = new Pins(TempStore.Unique("pins"));
        var pinFolders = new PinFolders(TempStore.Unique("pin-folders"));
        return (new ImportTarget(spaces, pins, pinFolders, Spaces.PersonalProfile, Space.FirstId) { ListsPins = rows, PinFolders = folders }, spaces, pins, pinFolders);
    }

    private static ArcSidebar Arc(string? profile = null) => Search.Core.Arc.Parse(Encoding.UTF8.GetBytes(ImportArcTests.Sidebar), profile)!;

    [Fact]
    public void BookmarksComeOnceAndReplacingTakesOutOnlyWhatCameBefore()
    {
        var marks = new Bookmarks(TempStore.Unique("bookmarks"), new Dictionary<string, string>());
        var records = new ImportRecords(new Dictionary<string, string>());
        BookmarkRead Read(bool complete = true) => new([Bookmark.Site("A", U("https://a.example/")), Bookmark.Folder("F", [Bookmark.Site("B", U("https://b.example/"))])], complete);
        Assert.Equal(new BookmarksTaken(2, 0, false), ImportTake.Bookmarks(marks, records, "Chrome", Read()));
        Assert.Equal(new BookmarksTaken(0, 2, false), ImportTake.Bookmarks(marks, records, "Chrome", Read()));
        marks.Add(U("https://mine.example/"), "Mine");
        Assert.Equal(new BookmarksTaken(0, 2, true), ImportTake.Bookmarks(marks, records, "Chrome", Read(complete: false), replacing: true));
        Assert.Equal(new BookmarksTaken(2, 0, false), ImportTake.Bookmarks(marks, records, "Chrome", Read(), replacing: true));
        Assert.Equal(["Mine", "A", "F"], marks.Roots.Select(n => n.Title));
        Assert.Equal(3, records.Of("Chrome")!.BookmarkIds.Count);
        Assert.Equal("2 bookmarks", new BookmarksTaken(2, 0, false).Said("Chrome"));
    }

    [Fact]
    public async Task PlacesGoIntoHistoryCountedAsTheyWere()
    {
        var history = new History(TempStore.Unique("history"));
        await history.Loaded;
        var records = new ImportRecords(new Dictionary<string, string>());
        var then = DateTimeOffset.UtcNow.AddDays(-2);
        Assert.Equal(1, ImportTake.Places(history, [new ImportedPlace(U("https://visited.example/"), "Visited", 7, then)], records, "Edge"));
        var trace = Assert.Single(history.Everything());
        Assert.Equal(("Visited", 7), (trace.Title, trace.Count));
        Assert.Equal(1, records.Of("Edge")!.Places);
    }

    [Fact]
    public void ArcsSpacesBecomeSpacesTheirPinnedListsTabsAsleepAndFavouritesPins()
    {
        var (target, spaces, pins, _) = Target();
        var taken = ImportTake.Sidebar(Arc(), target);
        Assert.Equal((2, 2, 3), (taken.Spaces, taken.Pins, taken.Tabs));
        Assert.True(spaces.UsesSpaces);
        var made = spaces.Of(Spaces.PersonalProfile);
        Assert.Equal(["Personal", "Work", "Raycast"], made.Select(s => s.Name));
        var work = made[1];
        Assert.Equal(("briefcase", true), (work.Symbol, work.SharesSignIns == true));
        Assert.Equal(["https://mail.google.com/"], pins.Defs(work.Id).Select(p => p.Home));
        Assert.Equal("M", pins.Defs(work.Id)[0].Letter);
        var row = Session.Read(work.Id);
        Assert.Equal(["https://github.com/", "https://docs.example/spec"], row.Tabs.Select(t => t.Url));
        var docs = Assert.Single(row.Groups!);
        Assert.Equal("Docs", docs.Name);
        Assert.Equal(docs.Id, row.Tabs[1].GroupId);
        Assert.Contains(U("https://raycast.com/"), taken.Brought);

        // Again: nothing twice.
        var again = ImportTake.Sidebar(Arc(), target);
        Assert.Equal((0, 0, 0), (again.Spaces, again.Pins, again.Tabs));
        Assert.Equal(3, spaces.Of(Spaces.PersonalProfile).Count);
    }

    [Fact]
    public void AnotherArcProfilesSpacesGoToTheProfileTheAppSays()
    {
        var (target, spaces, _, _) = Target();
        var work = Guid.NewGuid();
        var taken = ImportTake.Sidebar(Arc(), target with { ProfileFor = folder => folder == "Profile 1" ? work : null });
        Assert.Equal(2, taken.Spaces);
        Assert.Equal(["Raycast"], spaces.Of(work).Select(s => s.Name));
    }

    [Fact]
    public void WithPinnedRowsLoosePagesAreRowsAndFiledOnesTabsInTheirGroup()
    {
        var (target, _, pins, folders) = Target(rows: true);
        var asleep = new List<AsleepPage>();
        var taken = ImportTake.Sidebar(Arc("Default"), target with { Asleep = (_, pages) => { asleep.AddRange(pages); return (pages.Count, 0); } });
        Assert.Equal((0, 2, 1), (taken.Spaces, taken.Pins, taken.Tabs));
        var defs = pins.Defs(Space.FirstId);
        Assert.Equal([("https://mail.google.com/", (bool?)null), ("https://github.com/", true)], defs.Select(d => (d.Home, d.Listed)));
        Assert.Equal(new AsleepPage(U("https://docs.example/spec"), "Spec", "Docs"), Assert.Single(asleep));
        Assert.Empty(folders.Folders(Space.FirstId));
    }

    [Fact]
    public void WithFoldersInPinnedRowsItsFoldersComeAsFolders()
    {
        var (target, _, pins, folders) = Target(rows: true, folders: true);
        // A folder of the same name is filled rather than made again.
        var docs = new PinFolder(Guid.NewGuid(), "docs");
        folders.Set(Space.FirstId, [docs]);
        var taken = ImportTake.Sidebar(Arc("Default"), target with { Asleep = (_, _) => throw new InvalidOperationException("nothing asleep") });
        Assert.Equal((3, 0), (taken.Pins, taken.Tabs));
        var spec = pins.Defs(Space.FirstId).Single(d => d.Home == "https://docs.example/spec");
        Assert.Equal((true, (Guid?)docs.Id), (spec.Listed == true, spec.Folder));
        Assert.Equal([docs.Id], folders.Folders(Space.FirstId).Select(f => f.Id));
    }

    [Fact]
    public void FoldersDeeperThanPinnedRowsHoldGoIntoTheDeepestThatMay()
    {
        ArcNode Page(string name) => new ArcNode.Page(new ArcItem(name, U($"https://{name}.example/")));
        var nodes = new ArcNode[] { new ArcNode.Folder("A", [new ArcNode.Folder("B", [new ArcNode.Folder("C", [new ArcNode.Folder("D", [Page("deep")])])])]) };
        var (made, pages) = ImportTake.Filed(nodes, []);
        Assert.Equal(["A", "B", "C"], made.Select(f => f.Name));
        Assert.Equal(made[2].Id, Assert.Single(pages).Folder);
    }

    [Fact]
    public void OpenTabsComeAsleepGroupedAndPinsAsPins()
    {
        var (target, _, pins, _) = Target();
        var browser = new Browser();
        browser.Open(U("https://already.example/"));
        var research = new GroupToken(1, 2);
        var snapshot = new SessionSnapshot
        {
            Windows = [[new(U("https://already.example/"), "Already", false), new(U("https://one.example/"), "One", false, research), new(U("https://loose.example/"), "Loose", false), new(U("https://two.example/"), "Two", false, research), new(U("https://pin.example/"), "Pin", true)]],
            Groups = { [research] = new ImportedGroup("Research", true) },
        };
        var taken = ImportTake.OpenTabs(snapshot, target with { Asleep = (_, pages) => ImportTake.IntoBrowser(browser, pages) });
        Assert.Equal((1, 3, 1), (taken.Pins, taken.Tabs, taken.Groups));
        Assert.Equal(["https://pin.example/"], pins.Defs(Space.FirstId).Select(p => p.Home));
        Assert.Equal(["https://already.example/", "https://one.example/", "https://two.example/", "https://loose.example/"], browser.Tabs.Select(t => t.Url!.AbsoluteUri));
        var group = Assert.Single(browser.Groups);
        Assert.Equal(("Research", true), (group.Name, group.Collapsed));
        Assert.Equal(group.Id, browser.Tabs[2].GroupId);
    }

    [Fact]
    public void IntoASavedRowEachPageGoesAfterItsGroupAndTheFrontStays()
    {
        var work = new TabGroup(Guid.NewGuid(), "Work");
        var shape = new Session.Shape([new Session.Entry("https://pin.example/", "Pin") { Pin = "P" }, new Session.Entry("https://x.example/", "X") { GroupId = work.Id }, new Session.Entry("https://y.example/", "Y")], 2) { Groups = [work] };
        var (next, tabs, groups) = ImportTake.IntoShape(shape, [new(U("https://z.example/"), "Z", "Work"), new(U("https://w.example/"), "W"), new(U("https://x.example/"), "X again"), new(U("https://n.example/"), "N", "New", true)]);
        Assert.Equal((3, 1), (tabs, groups));
        Assert.Equal(["https://pin.example/", "https://x.example/", "https://z.example/", "https://y.example/", "https://w.example/", "https://n.example/"], next.Tabs.Select(t => t.Url));
        Assert.Equal("https://y.example/", next.Tabs[next.Active].Url);
        Assert.Equal(work.Id, next.Tabs[2].GroupId);
        Assert.True(next.Groups!.Single(g => g.Name == "New").Collapsed);
    }

    [Fact]
    public void IntoASpacesSessionFileItIsWrittenAtOnce()
    {
        var space = Guid.NewGuid();
        Assert.Equal((1, 0), ImportTake.IntoSession(space, [new(U("https://saved.example/"), "Saved")]));
        Assert.Equal(["https://saved.example/"], Session.Read(space).Tabs.Select(t => t.Url));
        Assert.Equal((0, 0), ImportTake.IntoSession(space, [new(U("https://saved.example/"), "Saved")]));
    }

    [Theory]
    [InlineData("https://mail.google.com/", "https://mail.google.com/mail/u/0/", true)]
    [InlineData("https://www.github.com/me/", "https://github.com/me/repo", true)]
    [InlineData("https://github.com/me", "https://github.com/media", false)]
    [InlineData("https://github.com/me", "https://github.com/work", false)]
    [InlineData("http://localhost:3000/", "http://localhost:5173/", false)]
    [InlineData("https://a.example/", "https://b.example/", false)]
    public void APinIsTheOneAPageWouldMakeOnTheSameSiteAndPath(string home, string page, bool same) =>
        Assert.Equal(same, ImportTake.SamePin(new PinDef(Guid.NewGuid(), "x", home, "x"), U(page)));
}
