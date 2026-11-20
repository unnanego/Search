using System.Text;
using System.Text.Json;
using Search.Core;

namespace Search.Core.Tests;

// Mostly Tests/HistoryRegression/main.swift, case for case, plus the file's shape against the Mac's.
public class HistoryTests
{
    public HistoryTests() => TempStore.Ready();

    private static Uri U(string address) => new(address);

    private static async Task<History> Fresh(string? file = null, string? contents = null)
    {
        file ??= TempStore.Unique("history");
        if (contents is not null)
        {
            Directory.CreateDirectory(Store.Folder);
            await File.WriteAllTextAsync(Store.File(file), contents);
        }
        var history = new History(file);
        await history.Loaded;
        return history;
    }

    private static IReadOnlyList<Trace> Traces(History history, string host) => history.Everything().Where(t => t.Url.Host == host).ToList();

    private static string Seconds(DateTimeOffset date) => SwiftJson.Seconds(date).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// A history.json as the Mac's JSONEncoder writes it.
    private static string MacFile(params (string Url, string Key, string Title, long Count, DateTimeOffset Last)[] visits) =>
        "[" + string.Join(",", visits.Select(v => $$"""{"url":"{{v.Url.Replace("/", "\\/")}}","key":"{{v.Key.Replace("/", "\\/")}}","title":"{{v.Title}}","count":{{v.Count}},"last":{{Seconds(v.Last)}}}""")) + "]";

    private static readonly DateTimeOffset Then = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static async Task<string> Written(History history, string file)
    {
        history.Flush();
        await Disk.Flush();
        return File.Exists(Store.File(file)) ? await File.ReadAllTextAsync(Store.File(file)) : "";
    }

    [Fact]
    public async Task RecordAndTakeKeepCanonicalIdentityComponents()
    {
        var history = await Fresh();
        string[] addresses = ["http://identity.example.test:8080/A?Q=One#first", "https://identity.example.test:8080/A?Q=One", "https://identity.example.test:9090/A?Q=One",
            "https://identity.example.test:8080/a?Q=One", "https://identity.example.test:8080/A?q=One", "https://identity.example.test:8080/A?Q=one"];
        history.Record(U(addresses[0]), "HTTP");
        for (var i = 1; i < addresses.Length; i++) history.Take(U(addresses[i]), $"Imported {i}", i + 1, Then.AddSeconds(i));
        // Fragments identify an in-page position, not a separate history entry.
        history.Take(U("http://identity.example.test:8080/A?Q=One#later"), "", 8, Then.AddSeconds(100));

        var pages = Traces(history, "identity.example.test").Where(t => t.Url.AbsolutePath is "/A" or "/a").ToList();
        Assert.Equal(6, pages.Count);
        Assert.Equal(6, pages.Select(p => p.Key).Distinct().Count());
        Assert.Equal(8, pages.Single(p => p.Url.Scheme == "http" && p.Url.Port == 8080).Count);
        Assert.All(pages, p => Assert.DoesNotContain("#", p.Key));

        var local = await Fresh();
        foreach (var address in (string[])["http://localhost:3000/a", "http://localhost:4000/a", "http://localhost:3000/A", "http://localhost:4000/A"]) local.Record(U(address), address);
        Assert.Equal(4, Traces(local, "localhost").Count(t => t.Url.AbsolutePath != "/"));
    }

    [Fact]
    public async Task RetitleAndForgetUseIndependentIdentities()
    {
        var history = await Fresh();
        var insecure = U("http://titles.example.test:8080/A?Q=One");
        history.Record(insecure, "Insecure title");
        history.Record(U("https://titles.example.test:8080/A?Q=One"), "Secure title");
        history.Retitle(insecure, "Retitled HTTP");
        var pages = Traces(history, "titles.example.test").Where(t => t.Url.AbsolutePath == "/A").ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal("Retitled HTTP", pages.Single(p => p.Url.Scheme == "http").Title);
        Assert.Equal("Secure title", pages.Single(p => p.Url.Scheme == "https").Title);

        history.Forget(pages.Single(p => p.Url.Scheme == "http").Key);
        Assert.Equal("https", Assert.Single(Traces(history, "titles.example.test"), t => t.Url.AbsolutePath == "/A").Url.Scheme);
        history.Forget();
        Assert.Empty(history.Everything());
        Assert.Empty(history.Suggestions("titles"));
    }

    [Fact]
    public async Task LegacyLoadRebuildsIdentityFromStoredUrl()
    {
        var history = await Fresh(contents: MacFile(
            ("https://legacy.example.test:8080/A?Q=One#first", "stale-collision", "Upper", 2, Then),
            ("https://legacy.example.test:8080/a?Q=One", "stale-collision", "Lower", 3, Then.AddSeconds(1)),
            ("https://legacy.example.test:8080/A?Q=One#again", "old-third-key", "Duplicate fragment", 7, Then.AddSeconds(2))));
        var pages = Traces(history, "legacy.example.test").Where(t => t.Url.AbsolutePath is "/A" or "/a").ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(9, pages.Single(p => p.Url.AbsolutePath == "/A").Count);
        Assert.Equal(3, pages.Single(p => p.Url.AbsolutePath == "/a").Count);
        Assert.All(pages, p => Assert.False(p.Key.StartsWith("stale-") || p.Key == "old-third-key"));
    }

    [Fact]
    public async Task RootCreditsKeepOriginAndDirectRootCountsOnce()
    {
        var history = await Fresh();
        foreach (var address in (string[])["http://origin.example.test:8080/page", "https://origin.example.test:8080/page", "https://origin.example.test:9090/page"]) history.Record(U(address), "Page");
        var roots = history.Suggestions("ORIGIN.EXAMPLE.TEST", 50).Where(s => s.Url.Host == "origin.example.test" && s.Url.AbsolutePath == "/");
        Assert.Equal(["http:8080", "https:8080", "https:9090"], roots.Select(s => $"{s.Url.Scheme}:{s.Url.Port}").Order());
        Assert.Equal(3, Traces(history, "origin.example.test").Count);

        var direct = await Fresh();
        direct.Record(U("https://home.example.test/"), "Home");
        Assert.Equal(1, Assert.Single(Traces(direct, "home.example.test"), t => t.Url.AbsolutePath == "/").Count);
        direct.Record(U("https://home.example.test/deep"), "Deep page");
        var home = Assert.Single(Traces(direct, "home.example.test"), t => t.Url.AbsolutePath == "/");
        Assert.Equal((2, "Home"), (home.Count, home.Title));

        var canonical = await Fresh();
        canonical.Record(U("HTTPS://Normalize.example.test"), "First form");
        canonical.Record(U("https://normalize.example.test/"), "Slash form");
        var normal = Assert.Single(Traces(canonical, "normalize.example.test"));
        Assert.Equal((2, "https://normalize.example.test/"), (normal.Count, normal.Key));
        Assert.Equal("https://normalize.example.test/", History.Identity(U("HTTPS://user:pw@Normalize.EXAMPLE.test#top")));
    }

    [Fact]
    public async Task SuggestionIdsAndSafeCaseInsensitiveCompletion()
    {
        var history = await Fresh();
        history.Record(U("https://suggest.example.test/A?Q=One"), "Upper page");
        history.Record(U("https://suggest.example.test/a?Q=One"), "Lower page");
        history.Record(U("https://suggest.example.test/A?q=One"), "Query case");
        var options = history.Suggestions("SUGGEST.EXAMPLE.TEST", 50);
        Assert.Contains(options, o => o.Url.AbsolutePath == "/A");
        Assert.Equal(options.Count, options.Select(o => o.Id).Distinct().Count());

        var upper = options.Single(o => o.Url.AbsolutePath == "/A" && o.Url.Query == "?Q=One");
        Assert.Equal("/A?Q=One", history.Completion("SUGGEST.EXAMPLE.TEST", [upper]));
        Assert.Null(history.Completion("suggest.example.test/a?Q=", [upper]));
        Assert.Null(history.Completion("suggest/A", [upper]));

        var schemes = await Fresh();
        schemes.Record(U("http://scheme-id.example.test:3000/a?Q=One"), "HTTP");
        schemes.Record(U("https://scheme-id.example.test:3000/a?Q=One"), "HTTPS");
        var both = schemes.Suggestions("scheme-id.example.test", 50).Where(s => s.Url.AbsolutePath == "/a" && s.Url.Query == "?Q=One").ToList();
        Assert.Equal(2, both.Select(s => s.Url.Scheme).Distinct().Count());
        Assert.Equal(2, both.Select(s => s.Id).Distinct().Count());
        foreach (var candidate in both)
        {
            var typed = candidate.Key[..^1];
            var ending = schemes.Completion(typed, [candidate]);
            Assert.NotNull(ending);
            Assert.Equal(History.Identity(candidate.Url), History.Identity(Address.UrlFrom(typed + ending)!));
        }

        var known = await Fresh();
        known.Record(U("https://github.com/"), "GitHub home");
        Assert.DoesNotContain(known.Suggestions("github", 50), s => s.Kind == SuggestionKind.Known);
    }

    [Fact]
    public async Task CredentialsNeverReachTheFile()
    {
        var file = TempStore.Unique("history");
        var history = await Fresh(file);
        history.Record(U("https://user:secret@cred.example.test/page"), "Signed in");
        history.Take(U("https://user:secret@cred.example.test/other"), "Taken", 2, DateTimeOffset.UtcNow);
        history.Retitle(U("https://user:secret@cred.example.test/page"), "Retitled");
        var pages = Traces(history, "cred.example.test");
        Assert.Equal(2, pages.Count);
        Assert.Equal("Retitled", pages.Single(p => p.Url.AbsolutePath == "/page").Title);
        Assert.All(pages, p => Assert.DoesNotContain("secret", p.Key + p.Url.AbsoluteUri + p.Address));
        Assert.DoesNotContain("secret", await Written(history, file));

        var legacyFile = TempStore.Unique("history");
        var legacy = await Fresh(legacyFile, MacFile(("https://user:secret@oldcred.example.test/page", "oldcred.example.test/page", "Old", 4, DateTimeOffset.UtcNow)));
        Assert.Equal(4, Traces(legacy, "oldcred.example.test").Single().Count);
        // Written again at once, with nothing else visited.
        await Task.Delay(2500);
        await Disk.Flush();
        var rewritten = await File.ReadAllTextAsync(Store.File(legacyFile));
        Assert.DoesNotContain("secret", rewritten);
        Assert.Contains("oldcred.example.test", rewritten);
    }

    [Fact]
    public async Task HandEditedCountsAreHeld()
    {
        var big = await Fresh(contents: MacFile(
            ("https://big.example.test/", "big.example.test", "Big", long.MaxValue, DateTimeOffset.UtcNow),
            ("https://big.example.test/#again", "big.example.test", "Big", long.MaxValue, DateTimeOffset.UtcNow),
            ("https://neg.example.test/", "neg.example.test", "Neg", -5, DateTimeOffset.UtcNow)));
        big.Record(U("https://big.example.test/"), "Big");
        Assert.Equal(History.MostVisits, Traces(big, "big.example.test").Single().Count);
        Assert.Equal(0, Traces(big, "neg.example.test").Single().Count);
    }

    [Fact]
    public async Task LegacyCreditsMoveToTheOriginTheirPagesUsed()
    {
        var history = await Fresh(contents: MacFile(
            ("https://site.example.test/", "site.example.test", "", 5, Then),
            ("https://www.site.example.test/a", "site.example.test/a", "A", 3, Then),
            ("https://www.site.example.test/b", "site.example.test/b", "B", 2, Then),
            ("https://localhost/", "localhost", "", 4, Then),
            ("http://localhost:3000/app", "localhost/app", "App", 4, Then),
            ("https://bare.example.test/", "bare.example.test", "", 6, Then),
            ("https://bare.example.test/x", "bare.example.test/x", "X", 6, Then),
            ("https://titled.example.test/", "titled.example.test", "Titled", 2, Then),
            ("https://www.titled.example.test/x", "titled.example.test/x", "X", 2, Then)));

        Assert.Equal("www.site.example.test", Assert.Single(history.Suggestions("site.example", 50), s => s.Url.AbsolutePath == "/").Url.Host);
        history.Record(U("https://www.site.example.test/c"), "C");
        var after = Assert.Single(history.Suggestions("site.example", 50), s => s.Url.AbsolutePath == "/");
        Assert.Equal("ample.test", history.Completion("site.ex", [after]));
        Assert.Equal("http://localhost:3000/", Assert.Single(history.Suggestions("localhost", 50), s => s.Url.AbsolutePath == "/").Url.AbsoluteUri);
        Assert.Equal("bare.example.test", Assert.Single(history.Suggestions("bare.example", 50), s => s.Url.AbsolutePath == "/").Url.Host);
        Assert.Equal("titled.example.test", Assert.Single(history.Suggestions("titled.example", 50), s => s.Url.AbsolutePath == "/").Url.Host);
    }

    [Fact]
    public async Task CompletionFinishesWwwSitesWithoutTheirWww()
    {
        var history = await Fresh();
        history.Record(U("https://www.apple-test.example/iPhone"), "iPhone");
        var options = history.Suggestions("apple-t", 3);
        Assert.Equal("est.example", history.Completion("apple-t", options));
        Assert.Equal("est.example", history.Completion("www.apple-t", options));
        var page = options.Where(o => o.Url.AbsolutePath == "/iPhone").ToList();
        Assert.Equal("hone", history.Completion("apple-test.example/iP", page));
        Assert.Null(history.Completion("apple-test.example/ip", page));
        history.Record(U("http://www.plain-test.example/"), "Plain");
        Assert.Null(history.Completion("plain-t", history.Suggestions("plain-t", 3)));
    }

    [Fact]
    public async Task OneLetterCompletesOnlyTheTopVisitedSite()
    {
        var history = await Fresh();
        for (var i = 0; i < 5; i++) history.Record(U("https://x.com/"), "X");
        history.Record(U("https://x.com/home"), "Home");
        var options = history.Suggestions("x", 3);
        Assert.Equal("x.com", options[0].Key);
        Assert.Equal(".com", history.Completion("x", options));
        Assert.Equal(".com", history.Completion("X", options));
        Assert.Null(history.Completion("x", options.Where(o => o.Url.AbsolutePath == "/home").ToList()));
        Assert.Null(history.Completion("q", history.Suggestions("q", 3)));
        history.Record(U("https://netflix-test.example/"), "N");
        Assert.Null(history.Completion("e", history.Suggestions("e", 3).Where(s => s.Key.StartsWith("netflix")).ToList()));
        Assert.Null(history.Completion("x", [new Suggestion("q-test.example", "", U("https://q-test.example/"), SuggestionKind.Known), options[0]]));
        Assert.Equal("com", history.Completion("x.", options));
        Assert.Null(history.Completion("", options));
    }

    [Fact]
    public async Task AFrontDoorLeadsItsDeepPages()
    {
        var history = await Fresh();
        history.Record(U("https://github.com/team/api/pulls/12"), "Fix the thing");
        history.Record(U("https://github.com/team/api/pulls/12"), "Fix the thing");
        var keys = history.Suggestions("git").Select(s => s.Key).ToList();
        Assert.Equal(["github.com", "github.com/team/api/pulls/12"], keys);
        Assert.Equal("hub.com", history.Completion("git", history.Suggestions("git")));
    }

    [Fact]
    public async Task TitlesPathsAndSlipsFillWhatTheAddressLeaves()
    {
        var history = await Fresh();
        history.Record(U("https://news.example-test.org/"), "Hacker Daily");
        history.Record(U("https://docs.example-test.org/guide/concurrency"), "Swift Guide");
        for (var i = 0; i < 3; i++) history.Record(U("https://hackerspace.example/"), "Space");

        var byTitle = history.Suggestions("daily");
        Assert.Contains(byTitle, s => s.Url.Host == "news.example-test.org");
        Assert.Contains(history.Suggestions("concurrency"), s => s.Url.AbsolutePath == "/guide/concurrency");
        Assert.Contains(history.Suggestions("concurency"), s => s.Url.AbsolutePath == "/guide/concurrency");
        Assert.Contains(history.Suggestions("guide swift"), s => s.Url.AbsolutePath == "/guide/concurrency");
        Assert.DoesNotContain(history.Suggestions("guide kotlin"), s => s.Url.AbsolutePath == "/guide/concurrency");

        // The address as typed always comes first, whatever the title says.
        var hacker = history.Suggestions("hacker");
        Assert.Equal("hackerspace.example", hacker[0].Url.Host);
        Assert.Contains(hacker, s => s.Url.Host == "news.example-test.org");
        Assert.Null(history.Completion("daily", byTitle));
        Assert.DoesNotContain(history.Suggestions("da"), s => s.Title.Contains("Daily"));
        Assert.Contains(history.Suggestions("dai"), s => s.Title.Contains("Daily"));

        var slip = await Fresh();
        slip.Record(U("https://wiki.example/notes"), "Pipline notes");
        for (var i = 0; i < 40; i++) slip.Record(U("https://git.example/team/api/-/pipelines"), "Pipelines");
        Assert.Equal("git.example/team/api/-/pipelines", slip.Suggestions("pipline")[0].Key);

        var tie = await Fresh();
        tie.Record(U("https://git.example/team/api/-/pipelines"), "");
        tie.Record(U("https://wiki.example/notes"), "Pipline");
        Assert.Equal("wiki.example/notes", tie.Suggestions("pipline")[0].Key);

        var many = await Fresh();
        for (var i = 0; i < 50; i++) many.Record(U("https://git.example/old/api"), "");
        for (var i = 0; i < 500; i++) many.Record(U("https://git.example/team/api"), "");
        Assert.Equal("git.example/team/api", many.Suggestions("api")[0].Key);

        // A page counts the visits to every page under it.
        var under = await Fresh();
        for (var i = 0; i < 5; i++) under.Record(U("https://git.example/team/api"), "team / api");
        for (var i = 0; i < 40; i++) under.Record(U("https://git.example/team/api/-/merge_requests/1"), "Fix api");
        for (var i = 0; i < 20; i++) under.Record(U("https://git.example/team/api/-/pipelines/2"), "Pipeline · api");
        under.Record(U("https://git.example/team/api-old/-/merge_requests/9"), "Old api");
        for (var i = 0; i < 5; i++) under.Record(U("https://rapid.example/"), "Rapid");
        var repo = under.Suggestions("api", 3).Select(s => s.Key).ToList();
        Assert.Equal("git.example/team/api", repo[0]);
        Assert.Equal("git.example/team/api/-/merge_requests/1", repo[1]);

        // As often visited, the fit decides.
        var fits = await Fresh();
        foreach (var page in (string[])["https://api.example/", "https://dev-api.example/", "https://git.example/team/api", "https://rapid.example/"]) fits.Record(U(page), "");
        var fit = fits.Suggestions("api").Select(s => s.Key).ToList();
        Assert.StartsWith("api.", fit[0]);
        Assert.StartsWith("dev-api.", fit[1]);
        Assert.True(fit.IndexOf("git.example/team/api") < fit.FindIndex(k => k.StartsWith("rapid")));

        // A name the app knows, typed with a slip, while nothing visited is near.
        Assert.Contains((await Fresh()).Suggestions("githb"), s => s.Key == "github.com" && s.Kind == SuggestionKind.Known);
        Assert.Empty((await Fresh()).Suggestions("   "));
    }

    [Fact]
    public async Task TitleChurnWaitsForTheNextWrite()
    {
        var file = TempStore.Unique("history");
        var path = Store.File(file);
        var history = await Fresh(file);
        var inbox = U("https://mail.example.test/inbox");
        history.Record(inbox, "");
        await Task.Delay(2200);
        await Disk.Flush();
        Assert.True(File.Exists(path));
        File.Delete(path);

        history.Retitle(inbox, "(1) Inbox");
        await Task.Delay(2200);
        await Disk.Flush();
        Assert.Contains("(1) Inbox", await File.ReadAllTextAsync(path));
        File.Delete(path);

        history.Retitle(inbox, "(2) Inbox");
        history.Retitle(inbox, "(3) Inbox");
        await Task.Delay(2200);
        await Disk.Flush();
        Assert.False(File.Exists(path));
        Assert.Contains("(3) Inbox", await Written(history, file));
    }

    [Fact]
    public async Task MemoryAndFileAreCapped()
    {
        var file = TempStore.Unique("history");
        var history = await Fresh(file);
        for (var i = 0; i < History.Cap + 200; i++) history.Record(U($"https://site{i}.example/"), $"Site {i}");
        Assert.Equal(History.Cap + 200, history.Everything().Count);
        // The file holds the cap alone, by score.
        using (var written = JsonDocument.Parse(await Written(history, file))) Assert.Equal(History.Cap, written.RootElement.GetArrayLength());

        // Past the slack, memory is cut back to the cap, and the newest stay whatever they score.
        for (var i = 0; i < 2; i++) history.Record(U("https://site0.example/"), "Site 0");
        for (var i = History.Cap + 200; i < History.Cap + History.Slack + 1; i++) history.Record(U($"https://site{i}.example/"), $"Site {i}");
        var left = history.Everything();
        Assert.Equal(History.Cap, left.Count);
        Assert.Contains(left, t => t.Url.Host == $"site{History.Cap + History.Slack}.example");
        Assert.Contains(left, t => t.Url.Host == "site0.example");
        Assert.DoesNotContain(left, t => t.Url.Host == "site1.example");
    }

    [Fact]
    public async Task ACorruptFileIsSetAside()
    {
        var file = TempStore.Unique("history");
        var history = await Fresh(file, "[{\"url\": 5}]");
        Assert.Empty(history.Everything());
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-*.json"));
        // And the next write starts a new one.
        history.Record(U("https://after.example/"), "After");
        Assert.Contains("after.example", await Written(history, file));
    }

    [Fact]
    public async Task AMacFileReadsAsTheSameVisits()
    {
        var last = SwiftJson.Date(785000000.123456)!.Value;
        var history = await Fresh(contents: MacFile(
            ("https://github.com/team/api", "https://github.com/team/api", "API", 3, last),
            ("https://github.com/", "https://github.com/", "GitHub", 7, last.AddDays(-1)),
            ("https://www.example.com", "https://www.example.com/", "", 1, last.AddDays(-2))));
        var traces = history.Everything();
        Assert.Equal(["https://github.com/team/api", "https://github.com/"], traces.Select(t => t.Key));
        var api = traces[0];
        Assert.Equal(("github.com/team/api", "API", 3, last), (api.Address, api.Title, api.Count, api.Last));
        Assert.Equal("github.com", history.Suggestions("git")[0].Key);
        Assert.Contains(history.Suggestions("example"), s => s.Key == "www.example.com");
    }

    [Fact]
    public async Task WritesTheMacsShape()
    {
        var file = TempStore.Unique("history");
        var history = await Fresh(file);
        history.Record(U("https://example.com/a?b=c#frag"), "Ünïcode & <tags>");
        var text = await Written(history, file);
        Assert.Contains(@"""url"":""https:\/\/example.com\/a?b=c#frag""", text);
        Assert.Contains("Ünïcode & <tags>", text);
        using var written = JsonDocument.Parse(text);
        var visits = written.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, visits.Count);
        foreach (var visit in visits)
        {
            Assert.Equal(["url", "key", "title", "count", "last"], visit.EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.Number, visit.GetProperty("last").ValueKind);
            Assert.InRange(SwiftJson.Date(visit.GetProperty("last").GetDouble())!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        }
        Assert.Contains(visits, v => v.GetProperty("key").GetString() == "https://example.com/a?b=c" && v.GetProperty("count").GetInt32() == 1);
        // And reads back as it was.
        var again = await Fresh(file);
        Assert.Equal("Ünïcode & <tags>", Assert.Single(again.Everything()).Title);
    }

    [Fact]
    public async Task ChangesDuringTheLoadAreReplayedOverTheFile()
    {
        var file = TempStore.Unique("history");
        Directory.CreateDirectory(Store.Folder);
        await File.WriteAllTextAsync(Store.File(file), MacFile(("https://old.example/", "https://old.example/", "Old", 2, Then)));
        var history = new History(file);
        history.Record(U("https://new.example/"), "New");
        await history.Loaded;
        Assert.Equal(["New", "Old"], history.Everything().Select(t => t.Title));

        var cleared = new History(file);
        cleared.Forget();
        cleared.Record(U("https://after.example/"), "After");
        await cleared.Loaded;
        Assert.Equal("After", Assert.Single(cleared.Everything()).Title);
    }

    [Fact]
    public async Task RecentIsTheNewestEightAndChangedIsRaised()
    {
        var history = await Fresh();
        var changes = 0;
        history.Changed += () => changes++;
        for (var i = 0; i < 10; i++) history.Take(U($"https://p{i}.example/x"), $"P{i}", 1, Then.AddSeconds(i));
        Assert.Equal(10, changes);
        Assert.Equal(Enumerable.Range(2, 8).Reverse().Select(i => $"P{i}"), history.Recent().Select(t => t.Title));
        history.Retitle(U("https://p9.example/x"), "Nine");
        Assert.Equal("Nine", history.Recent()[0].Title);
        history.Retitle(U("https://p9.example/x"), "Nine");
        Assert.Equal(11, changes);
    }

    [Fact]
    public void SuggestionIdsFollowTheirKind()
    {
        var url = U("https://Example.com/a#x");
        Assert.Equal("https://example.com/a", new Suggestion("example.com/a", "", url, SuggestionKind.Visited).Id);
        Assert.Equal("command Settings", new Suggestion("Settings", "", U("about:blank"), SuggestionKind.Command).Id);
        Assert.Equal("Settings", new Suggestion("Settings", "", url, SuggestionKind.Search).Id);
        Assert.Null(History.Kept(new Uri("/relative", UriKind.Relative)));
        Assert.Equal("https://example.com/a#x", History.Kept(U("https://me:pw@example.com/a#x"))!.AbsoluteUri);
    }
}
