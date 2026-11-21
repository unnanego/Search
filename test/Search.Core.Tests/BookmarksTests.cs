using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class BookmarksTests
{
    private const string Work = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Hub = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Empty = "1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB";
    private const string News = "C9A646D3-9C61-4CB7-BFCD-EE2522C8F633";

    public BookmarksTests() => TempStore.Ready();

    /// As a Mac's JSONEncoder writes it: slashes escaped, UUIDs uppercase, no url for a folder, no children for a
    /// site, and a key this version doesn't know.
    private const string MacFile = $$"""
        [{"id":"{{Work}}","title":"Work","children":[{"id":"{{Hub}}","title":"GitHub","url":"https:\/\/github.com"},{"id":"{{Empty}}","title":"Later","children":[]}]},
         {"id":"{{News}}","title":"","url":"https:\/\/news.ycombinator.com\/news","icon":"ignored"}]
        """;

    private static (Bookmarks Bookmarks, string File) Fresh(string? contents = null, IDictionary<string, string>? settings = null)
    {
        var file = TempStore.Unique("bookmarks");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Bookmarks(file, settings ?? new Dictionary<string, string>()), file);
    }

    private static Uri U(string text) => new(text);

    private static async Task<List<Bookmark>> Written(string file)
    {
        await Disk.Flush();
        return Bookmarks.Decode(await File.ReadAllBytesAsync(Store.File(file))) ?? throw new Xunit.Sdk.XunitException("didn't decode");
    }

    [Fact]
    public void AMacFileReads()
    {
        var (bookmarks, _) = Fresh(MacFile);
        Assert.Equal(2, bookmarks.Roots.Count);
        var work = bookmarks.Roots[0];
        Assert.True(work.IsFolder);
        Assert.Equal((Guid.Parse(Work), "Work"), (work.Id, work.Title));
        var hub = work.Children![0];
        Assert.Equal((Guid.Parse(Hub), "GitHub", "https://github.com"), (hub.Id, hub.Title, hub.Url));
        Assert.True(work.Children[1].IsFolder);
        Assert.Empty(work.Children[1].Children!);
        var news = bookmarks.Roots[1];
        Assert.True(news.Unnamed);
        Assert.Equal("news.ycombinator.com/news", news.Label);
        Assert.Equal("N", news.Letter);
        Assert.Null(news.Children);
        Assert.Equal(2, bookmarks.Count);
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var nodes = new List<Bookmark>
        {
            new(Guid.Parse(Work), "Wörk", null, [new Bookmark(Guid.Parse(Hub), "GitHub", "https://github.com/")]),
            new(Guid.Parse(News), "", "https://news.ycombinator.com/news"),
        };
        Assert.Equal(
            $$"""[{"id":"{{Work}}","title":"Wörk","children":[{"id":"{{Hub}}","title":"GitHub","url":"https:\/\/github.com\/"}]},{"id":"{{News}}","title":"","url":"https:\/\/news.ycombinator.com\/news"}]""",
            Encoding.UTF8.GetString(Bookmarks.Encode(nodes)));
    }

    [Fact]
    public void AMacFileComesBackTheSame()
    {
        var once = Bookmarks.Decode(Encoding.UTF8.GetBytes(MacFile))!;
        Assert.Equal(once, Bookmarks.Decode(Bookmarks.Encode(once))!);
    }

    [Theory]
    [InlineData("""[{"title":"no id"}]""")]
    [InlineData("""[{"id":"not-a-uuid","title":"x"}]""")]
    [InlineData("""[{"id":"0E984725-C51C-4BF4-9960-E1C80E27ABA0","title":"x","url":5}]""")]
    [InlineData("""[{"id":"0E984725-C51C-4BF4-9960-E1C80E27ABA0","title":"x","children":[{"title":"bad child"}]}]""")]
    [InlineData("""{"not":"a list"}""")]
    [InlineData("""not json""")]
    public void WhatSwiftWontDecodeDoesNot(string json) => Assert.Null(Bookmarks.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void NullIsNotThere() =>
        Assert.True(Bookmarks.Decode(Encoding.UTF8.GetBytes($$"""[{"id":"{{Work}}","title":"x","url":null,"children":null}]"""))![0].IsFolder);

    [Fact]
    public async Task ACorruptFileIsSetAside()
    {
        var (bookmarks, file) = Fresh("""[{"title":"no id"}]""");
        Assert.True(bookmarks.IsEmpty);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-*.json"));
        bookmarks.Add(U("https://after.example/"), "After");
        Assert.Equal("After", Assert.Single(await Written(file)).Title);
    }

    [Fact]
    public async Task AddingFindsAndKeeps()
    {
        var (bookmarks, file) = Fresh(MacFile);
        // Written on the Mac without the slash Uri gives a bare host: still the same page.
        Assert.True(bookmarks.Contains(U("https://github.com/")));
        Assert.Equal(Guid.Parse(Hub), bookmarks.Find(U("https://github.com"))!.Id);
        Assert.Null(bookmarks.Add(U("https://github.com/"), "Again"));

        var made = bookmarks.Add(U("https://www.example.com/a"), "")!;
        Assert.Equal("example.com/a", made.Title);
        Assert.Equal("https://www.example.com/a", made.Url);
        Assert.Same(made, bookmarks.Roots[^1]);
        Assert.True(bookmarks.Contains(U("https://www.example.com/a")));
        Assert.Equal(3, (await Written(file)).Count);
        Assert.Equal(made, bookmarks.Find(made.Id));
        Assert.Null(bookmarks.Find(Guid.NewGuid()));
    }

    [Fact]
    public void ChangesAreHeard()
    {
        var (bookmarks, _) = Fresh();
        var heard = new List<string?>();
        ((INotifyPropertyChanged)bookmarks).PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        bookmarks.Add(U("https://a.example/"), "A");
        Assert.Equal([nameof(Bookmarks.Roots)], heard);
    }

    [Fact]
    public async Task RemovingTakesItFromWhereverItIs()
    {
        var (bookmarks, file) = Fresh(MacFile);
        bookmarks.Remove(Guid.Parse(Hub));
        Assert.False(bookmarks.Contains(U("https://github.com/")));
        Assert.Single(bookmarks.Roots[0].Children!);
        bookmarks.Remove(Guid.Parse(Work));
        Assert.Single(bookmarks.Roots);
        Assert.Single(await Written(file));
    }

    [Fact]
    public void PathsAndParents()
    {
        var (bookmarks, _) = Fresh(MacFile);
        Assert.Equal([Guid.Parse(Work)], bookmarks.PathTo(Guid.Parse(Hub))!.Select(b => b.Id));
        Assert.Empty(bookmarks.PathTo(Guid.Parse(News))!);
        Assert.Null(bookmarks.PathTo(Guid.NewGuid()));
        Assert.Equal(Guid.Parse(Work), bookmarks.Parent(Guid.Parse(Empty)));
        Assert.Null(bookmarks.Parent(Guid.Parse(Work)));
        Assert.Equal([(Guid.Parse(Work), 0), (Guid.Parse(Empty), 1)], Bookmarks.Folders(bookmarks.Roots).Select(f => (f.Node.Id, f.Depth)));
        Assert.Equal([Guid.Parse(Empty)], bookmarks.MoveTargets(bookmarks.Find(Guid.Parse(News))!).Select(f => f.Node.Id).Skip(1));
        Assert.Empty(bookmarks.MoveTargets(bookmarks.Find(Guid.Parse(Work))!));
    }

    [Fact]
    public void Moving()
    {
        var (bookmarks, _) = Fresh();
        var a = bookmarks.Add(U("https://a.example/"), "A")!;
        var b = bookmarks.Add(U("https://b.example/"), "B")!;
        var c = bookmarks.Add(U("https://c.example/"), "C")!;
        string Order() => string.Join("", bookmarks.Roots.Select(n => n.Title));

        // Dropped just below itself (before what is now at 1, B), it stays where it was.
        bookmarks.Move(a.Id, null, 1);
        Assert.Equal("ABC", Order());
        bookmarks.Move(a.Id, null, 2);
        Assert.Equal("BAC", Order());
        bookmarks.Move(c.Id, null, 0);
        Assert.Equal("CBA", Order());
        bookmarks.Move(a.Id, null, 99);
        Assert.Equal("CBA", Order());

        var folder = bookmarks.Insert(Bookmark.Folder("F", []), null);
        var inner = bookmarks.Insert(Bookmark.Folder("Inner", []), folder.Id);
        bookmarks.Move(b.Id, folder.Id, 0);
        Assert.Equal(["B", "Inner"], bookmarks.Find(folder.Id)!.Children!.Select(n => n.Title));
        Assert.Equal(folder.Id, bookmarks.Parent(b.Id));

        // A folder never goes inside itself, and onto itself is nothing to do.
        var before = bookmarks.Roots;
        bookmarks.Move(folder.Id, inner.Id);
        bookmarks.Move(folder.Id, folder.Id);
        Assert.Same(before, bookmarks.Roots);
        // Into a folder that isn't there, nothing either.
        bookmarks.Move(a.Id, Guid.NewGuid());
        Assert.Same(before, bookmarks.Roots);
        // Nor into a site.
        bookmarks.Move(a.Id, c.Id);
        Assert.Same(before, bookmarks.Roots);
    }

    [Fact]
    public void InsertingIntoAFolderThatIsntThereFilesAtTheTop()
    {
        var (bookmarks, _) = Fresh(MacFile);
        var made = bookmarks.Insert(Bookmark.Site("x", U("https://x.example/")), Guid.NewGuid(), 0);
        Assert.Same(made, bookmarks.Roots[0]);
    }

    [Fact]
    public void RenamingAndReaddressing()
    {
        var (bookmarks, _) = Fresh(MacFile);
        bookmarks.Rename(Guid.Parse(Hub), "  Code  ");
        Assert.Equal("Code", bookmarks.Find(Guid.Parse(Hub))!.Title);
        bookmarks.Rename(Guid.Parse(Hub), "   ");
        Assert.True(bookmarks.Find(Guid.Parse(Hub))!.Unnamed);
        Assert.Equal("github.com", bookmarks.Find(Guid.Parse(Hub))!.Label);

        Assert.True(bookmarks.SetAddress(Guid.Parse(Hub), "gitlab.com"));
        Assert.Equal("https://gitlab.com/", bookmarks.Find(Guid.Parse(Hub))!.Url);
        Assert.False(bookmarks.SetAddress(Guid.Parse(Hub), "two words"));
        Assert.False(bookmarks.SetAddress(Guid.Parse(Work), "example.com"));

        // A folder keeps having no address.
        bookmarks.Update(Guid.Parse(Work), "Job", "https://nope.example/");
        var work = bookmarks.Find(Guid.Parse(Work))!;
        Assert.Equal(("Job", true), (work.Title, work.IsFolder));
    }

    [Fact]
    public void Searching()
    {
        var (bookmarks, _) = Fresh(MacFile);
        var found = Assert.Single(bookmarks.Matches("HUB git"));
        Assert.Equal(Guid.Parse(Hub), found.Node.Id);
        Assert.Equal(["Work"], found.Path);
        Assert.Equal(Guid.Parse(Work), Assert.Single(bookmarks.Matches("work")).Node.Id);
        Assert.Empty(bookmarks.Matches("  "));
    }

    [Fact]
    public void PlacesForTheAddressField()
    {
        var (bookmarks, _) = Fresh();
        bookmarks.Add(U("https://docs.example.com/guide"), "Handbook");
        bookmarks.Add(U("https://github.com/"), "GitHub");
        bookmarks.Add(U("https://example.org/longer/path"), "My github notes");
        bookmarks.Insert(new Bookmark(Guid.NewGuid(), "Bookmarklet", "javascript:alert(1)"), null);

        // The address starting with it first, then a word of the name.
        var (found, slips) = bookmarks.Places("git", 5);
        Assert.Equal(["https://github.com/", "https://example.org/longer/path"], found.Select(s => s.Url.AbsoluteUri));
        Assert.Empty(slips);
        Assert.Equal(("github.com", "GitHub", SuggestionKind.Bookmark), (found[0].Key, found[0].Title, found[0].Kind));

        // A typo comes apart, and one letter is too few.
        var typo = bookmarks.Places("handbok", 5);
        Assert.Empty(typo.Found);
        Assert.Equal("Handbook", Assert.Single(typo.Slips).Title);
        Assert.Empty(bookmarks.Places("g", 5).Found);
        Assert.Empty(bookmarks.Places("alert", 5).Found);
        Assert.Single(bookmarks.Places("git", 1).Found);
    }

    [Fact]
    public void TakingFromOtherBrowsers()
    {
        var settings = new Dictionary<string, string>();
        var (bookmarks, _) = Fresh(settings: settings);
        List<Bookmark> Chrome(params string[] dev) => [Bookmark.Site("A", U("https://a.example/")), Bookmark.Folder("Dev", dev.Select(d => Bookmark.Site(d, U($"https://{d.ToLowerInvariant()}.example/"))).ToList())];

        // The first fills the empty top level.
        Assert.Equal((2, 0), bookmarks.Take(Chrome("B"), "Chrome"));
        Assert.Equal("Chrome", settings["bookmarks.top"]);
        // Again: only what is new, a folder of the same name gone into.
        Assert.Equal((1, 2), bookmarks.Take(Chrome("B", "C"), "Chrome"));
        Assert.Equal(["B", "C"], bookmarks.Roots[1].Children!.Select(b => b.Title));

        // Another browser is kept apart in a folder of its name, and can be taken back out exactly.
        var taken = bookmarks.TakeNoting([Bookmark.Site("A", U("https://a.example/")), Bookmark.Site("Z", U("https://z.example/"))], "Firefox");
        Assert.Equal((2, 0), (taken.Added, taken.Already));
        var firefox = bookmarks.Roots[^1];
        Assert.Equal(("Firefox", 2), (firefox.Title, firefox.Children!.Count));
        Assert.Contains(firefox.Id, taken.Ids);
        Assert.Equal(2, bookmarks.Withdraw(taken.Ids));
        Assert.DoesNotContain(bookmarks.Roots, b => b.Title == "Firefox");
        Assert.Equal(3, bookmarks.Count);
        Assert.Equal((0, 0), bookmarks.Take([], "Safari"));
    }

    [Fact]
    public void AFolderOfTheirsYouFiledIntoStays()
    {
        var (bookmarks, _) = Fresh(settings: new Dictionary<string, string> { ["bookmarks.top"] = "Chrome" });
        bookmarks.Add(U("https://mine.example/"), "Mine");
        var taken = bookmarks.TakeNoting([Bookmark.Folder("Dev", [Bookmark.Site("B", U("https://b.example/"))])], "Chrome");
        var dev = bookmarks.Roots.Single(b => b.Title == "Dev");
        bookmarks.Insert(Bookmark.Site("Own", U("https://own.example/")), dev.Id);
        Assert.Equal(1, bookmarks.Withdraw(taken.Ids));
        Assert.Equal("Own", Assert.Single(bookmarks.Roots.Single(b => b.Title == "Dev").Children!).Title);
    }

    [Fact]
    public void ABrowserMostlyAlreadyHereGoesToTheTop()
    {
        var settings = new Dictionary<string, string>();
        var (bookmarks, _) = Fresh(settings: settings);
        bookmarks.Add(U("https://a.example/"), "A");
        Assert.Equal((1, 1), bookmarks.Take([Bookmark.Site("A", U("https://a.example")), Bookmark.Site("B", U("https://b.example/"))], "Edge"));
        Assert.Equal(["A", "B"], bookmarks.Roots.Select(b => b.Title));
        Assert.Equal("Edge", settings["bookmarks.top"]);
    }

    [Fact]
    public void TheLastFolderPicked()
    {
        var settings = new Dictionary<string, string>();
        var (bookmarks, file) = Fresh(MacFile, settings);
        Assert.Null(bookmarks.LastFolder);
        bookmarks.FileInto(Guid.Parse(News), Guid.Parse(Empty));
        Assert.Equal(Guid.Parse(Empty), bookmarks.LastFolder);
        Assert.Equal(Empty, settings["bookmarks.lastFolder." + file]);
        Assert.Equal(Guid.Parse(Empty), bookmarks.Parent(Guid.Parse(News)));
        // Once that folder is gone, the top level again.
        bookmarks.Remove(Guid.Parse(Empty));
        Assert.Null(bookmarks.LastFolder);
        bookmarks.LastFolder = null;
        Assert.False(settings.ContainsKey("bookmarks.lastFolder." + file));
    }

    [Fact]
    public async Task AdoptingAndRetiring()
    {
        var (bookmarks, file) = Fresh(MacFile);
        var saves = 0;
        void Count(Bookmarks b) { if (b == bookmarks) saves++; }
        Bookmarks.Saved += Count;
        try
        {
            bookmarks.Adopt(Bookmarks.Decode(Encoding.UTF8.GetBytes(MacFile))!);
            Assert.Equal(0, saves);
            bookmarks.Adopt([Bookmark.Site("Only", U("https://only.example/"))]);
            Assert.Equal(1, saves);
            Assert.Equal("Only", Assert.Single(await Written(file)).Title);

            bookmarks.Retire();
            bookmarks.Add(U("https://later.example/"), "Later");
            await Disk.Flush();
            Assert.False(File.Exists(Store.File(file)));
            Assert.Equal(1, saves);
        }
        finally { Bookmarks.Saved -= Count; }
    }

    [Fact]
    public void ForTheBarAndTheDropdown()
    {
        var (bookmarks, _) = Fresh(MacFile);
        Assert.Same(bookmarks.Roots, bookmarks.Bar);
        Assert.Empty(Bookmarks.Opening(bookmarks.Roots));
        var only = Bookmarks.Decode(Encoding.UTF8.GetBytes(MacFile))!.Take(1).ToList();
        Assert.Equal([Guid.Parse(Work)], Bookmarks.Opening(only));
        // A folder's own sites only, not its folders'.
        Assert.Equal([U("https://github.com/")], Bookmarks.OpenAll(bookmarks.Roots[0]));
        Assert.Equal("https://github.com", bookmarks.Roots[0].Children![0].Hint);
        Assert.Equal("Work", bookmarks.Roots[0].Hint);
    }

    [Fact]
    public void AskingBeforeRemoving()
    {
        var (bookmarks, _) = Fresh(MacFile);
        Assert.Equal("The bookmark in it goes too.", Bookmarks.RemovalWarning(bookmarks.Roots[0]));
        Assert.Null(Bookmarks.RemovalWarning(bookmarks.Roots[1]));
        Assert.Null(Bookmarks.RemovalWarning(bookmarks.Roots[0].Children![1]));
        Assert.Equal("The empty folders in it go too.", Bookmarks.RemovalWarning(Bookmark.Folder("F", [Bookmark.Folder("G", [])])));
        Assert.Equal("The 2 bookmarks in it go too.", Bookmarks.RemovalWarning(Bookmark.Folder("F", [Bookmark.Site("a", U("https://a.example/")), Bookmark.Site("b", U("https://b.example/"))])));
    }

    [Fact]
    public void AFilePerProfile()
    {
        Assert.Equal("bookmarks.json", Bookmarks.FileFor(Guid.Parse("00000000-0000-0000-0000-0000000000A1")));
        Assert.Equal($"bookmarks-{Work}.json", Bookmarks.FileFor(Guid.Parse(Work)));
    }
}
