using System.Security.Cryptography;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class ImportChromiumTests
{
    private static readonly string StoreId = new('a', 32), Unpacked = new('b', 32), Theme = new('c', 32), Builtin = new('d', 32), EdgeStore = new('e', 32);

    /// 2024-01-01 in Chromium's microseconds since 1601.
    private const long Then = 13348540800000000;

    /// Chrome with two profiles, "Default" (Personal) and "Profile 1" (Work, used last), and its key wrapped by DPAPI.
    private static (ChromiumSource Chrome, byte[] Key) MakeChrome(ImportFixture at)
    {
        var root = at.Path("Local", "Google", "Chrome", "User Data");
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] blob = [.. "DPAPI"u8.ToArray(), .. Dpapi.Protect(key)!];
        var wrapped = Convert.ToBase64String(blob);
        at.Write(Path.Combine(root, "Local State"), $$$"""
            {"os_crypt":{"encrypted_key":"{{{wrapped}}}"},
             "profile":{"info_cache":{"Default":{"name":"Personal"},"Profile 1":{"name":"Work"},"Profile 10":{"name":""}},"last_used":"Profile 1"}}
            """);
        at.Write(Path.Combine(root, "Default", "Bookmarks"), """
            {"roots":{
              "bookmark_bar":{"type":"folder","children":[
                {"type":"url","name":"GitHub","url":"https://github.com/"},
                {"type":"folder","name":"Dev","children":[{"type":"url","name":"Docs","url":"https://me:pw@docs.example/a"},{"type":"url","name":"JS","url":"javascript:alert(1)"}]}]},
              "other":{"type":"folder","children":[{"type":"url","name":"Other one","url":"https://other.example/"}]},
              "synced":{"type":"folder","children":[]}},
             "version":1}
            """);
        ImportFixture.Db(Path.Combine(root, "Default", "History"), "CREATE TABLE urls (id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER, last_visit_time INTEGER, hidden INTEGER)");
        var history = Path.Combine(root, "Default", "History");
        ImportFixture.Run(history, "INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES (?, ?, ?, ?, 0)", "https://news.example/", "News", 12, Then);
        ImportFixture.Run(history, "INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES (?, ?, ?, ?, 0)", "https://old.example/", "Old", 1, Then - 86_400_000_000);
        ImportFixture.Run(history, "INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES (?, ?, ?, ?, 0)", "chrome://settings/", "Settings", 3, Then);
        ImportFixture.Run(history, "INSERT INTO urls (url, title, visit_count, last_visit_time, hidden) VALUES (?, ?, ?, ?, 1)", "https://hidden.example/", "Hidden", 3, Then);
        var logins = ImportFixture.Db(Path.Combine(root, "Default", "Login Data"), "CREATE TABLE logins (origin_url VARCHAR NOT NULL, username_value VARCHAR, password_value BLOB, blacklisted_by_user INTEGER NOT NULL, date_last_used INTEGER)");
        void Login(string origin, string user, byte[] blob, bool never = false) => ImportFixture.Run(logins, "INSERT INTO logins VALUES (?, ?, ?, ?, ?)", origin, user, blob, never ? 1 : 0, Then);
        Login("https://www.example.com/login", "ann", ImportFixture.V10("hunter2", key));
        Login("http://plain.example/", "bob", ImportFixture.V10("clear-text-site", key));
        Login("https://bound.example/", "cat", [.. "v20"u8.ToArray(), .. RandomNumberGenerator.GetBytes(40)]);
        Login("https://old.example/", "dan", Dpapi.Protect("from-2019"u8.ToArray())!);
        Login("https://never.example/", "", [], never: true);
        Login("android://hash@com.vendor.app/", "eve", ImportFixture.V10("app", key));
        Login("https://lying.example/", "fay", "in the clear"u8.ToArray());
        at.Write(Path.Combine(root, "Default", "Preferences"), $$$"""
            {"extensions":{"settings":{
              "{{{StoreId}}}":{"location":1,"manifest":{"update_url":"https://clients2.google.com/service/update2/crx"}},
              "{{{Unpacked}}}":{"location":4},
              "{{{Theme}}}":{"from_webstore":true,"manifest":{"theme":{} } },
              "{{{Builtin}}}":{"location":1,"was_installed_by_default":true},
              "{{{EdgeStore}}}":{"location":1,"manifest":{"update_url":"https://edge.microsoft.com/extensionwebstorebase/v1/crx"}},
              "not-an-id":{"location":1} } } }
            """);
        at.Write(Path.Combine(root, "Profile 1", "Bookmarks"), """{"roots":{"bookmark_bar":{"children":[{"type":"url","name":"Jira","url":"https://jira.example/"}]}}}""");
        at.Write(Path.Combine(root, "Profile 10", "Bookmarks"), """{"roots":{}}""");
        // Not profiles: no file of one in them.
        Directory.CreateDirectory(Path.Combine(root, "Crashpad"));
        at.Write(Path.Combine(root, "System Profile", "History"), "");
        return ((ChromiumSource)ImportSource.Known(at.Roots).First(s => s.Name == "Chrome"), key);
    }

    [Fact]
    public void FindsOnlyTheBrowsersThatAreThere()
    {
        using var at = new ImportFixture();
        MakeChrome(at);
        Assert.Equal(["Chrome"], ImportSource.Installed(at.Roots).Select(s => s.Name));
    }

    [Fact]
    public void ProfilesGoByTheirNamesInAPersonsOrder()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        Assert.Equal([("Default", "Personal"), ("Profile 1", "Work"), ("Profile 10", "Profile 10")], chrome.Profiles.Select(p => (p.Id, p.Name)));
        Assert.Equal("Profile 1", chrome.Usual);
    }

    [Fact]
    public void ProfileOrderIsNatural() => Assert.True(ImportSource.Natural("Profile 2", "Profile 10") < 0 && ImportSource.Natural("Default", "Profile 1") < 0);

    [Fact]
    public void BookmarksComeBarFirstThenOtherAsAFolder()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        var read = chrome.Bookmarks("Default");
        Assert.True(read.Complete);
        Assert.Equal(["GitHub", "Dev", "Other"], read.Nodes.Select(n => n.Title));
        var dev = read.Nodes[1];
        Assert.Equal("https://docs.example/a", Assert.Single(dev.Children!).Url);
        Assert.Equal("https://other.example/", read.Nodes[2].Children![0].Url);
        Assert.Equal(4, chrome.Bookmarks().Nodes.Sum(n => n.IsFolder ? Bookmarks.CountOf(n.Children!) : 1));
    }

    [Fact]
    public void ABookmarksFileThatDoesntReadIsNotAnEmptyBrowser()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        at.Write(Path.Combine(chrome.Root, "Profile 1", "Bookmarks"), "{ torn");
        Assert.False(chrome.Bookmarks().Complete);
        Assert.True(chrome.Bookmarks("Default").Complete);
    }

    [Fact]
    public void HistoryComesNewestFirstWithItsDatesAndCounts()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        var places = chrome.Places("Default");
        Assert.Equal(["https://news.example/", "https://old.example/"], places.Select(p => p.Url.AbsoluteUri));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), places[0].Last);
        Assert.Equal(12, places[0].Count);
        Assert.Single(chrome.Places("Default", limit: 1));
    }

    [Fact]
    public void OnlyExtensionsFromTheChromeWebStoreCome()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        Assert.Equal([StoreId], chrome.Extensions("Default"));
    }

    [Fact]
    public void WithoutPreferencesTheExtensionsFolderSays()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        Directory.CreateDirectory(Path.Combine(chrome.Root, "Profile 1", "Extensions", Unpacked));
        Directory.CreateDirectory(Path.Combine(chrome.Root, "Profile 1", "Extensions", "Temp"));
        Assert.Equal([Unpacked], chrome.Extensions("Profile 1"));
    }

    [Fact]
    public void PasswordsDecryptWithTheKeyDpapiUnwraps()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        var found = chrome.Passwords("Default");
        Assert.Equal(LoginTrouble.None, found.Trouble);
        Assert.Equal([("example.com", "ann", "hunter2", false), ("plain.example", "bob", "clear-text-site", true), ("old.example", "dan", "from-2019", false)],
            found.Logins.Select(l => (l.Host, l.User, l.Password, l.Clear)));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), found.Logins[0].Used);
        Assert.Equal(["never.example"], found.Never);
        // The Android app's has no website; the one lying there in the clear isn't taken.
        Assert.Equal(1, found.Skipped);
        Assert.Equal(1, found.Undecrypted);
        Assert.DoesNotContain("hunter2", found.Logins[0].ToString());
    }

    [Fact]
    public void AppBoundPasswordsAreCountedAndSayToExport()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        var found = chrome.Passwords("Default");
        Assert.Equal(1, found.AppBound);
        Assert.Contains("chrome://password-manager/settings", found.Advice);
        Assert.DoesNotContain(found.Logins, l => l.Host == "bound.example");
    }

    [Fact]
    public void WithoutItsKeyNothingDecrypts()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        at.Write(Path.Combine(chrome.Root, "Local State"), "{}");
        var found = chrome.Passwords("Default");
        Assert.Equal(["old.example"], found.Logins.Select(l => l.Host));
        Assert.Equal(3, found.Undecrypted);
    }

    [Fact]
    public void AV10BlobWithTheWrongKeyDoesntDecrypt()
    {
        var blob = ImportFixture.V10("x", RandomNumberGenerator.GetBytes(32));
        Assert.Null(ChromiumSource.Unwrap(blob, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void ThePasswordAfterASiteHashStillComes()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] plain = [.. new byte[] { 0xff, 0xfe }, .. RandomNumberGenerator.GetBytes(30), .. "behind-a-hash"u8.ToArray()];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var sealedText = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, sealedText, tag);
        Assert.Equal("behind-a-hash", ChromiumSource.Unwrap([.. "v11"u8.ToArray(), .. nonce, .. sealedText, .. tag], key));
    }

    [Fact]
    public void YandexsPasswordsArentRead()
    {
        using var at = new ImportFixture();
        var yandex = (ChromiumSource)ImportSource.Known(at.Roots).First(s => s.Name == "Yandex");
        Assert.False(yandex.ImportsPasswords);
        Assert.Equal(LoginTrouble.Unreadable, yandex.Passwords().Trouble);
    }

    [Fact]
    public void PreviewCountsFromTheFilesAlone()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        // No key to be had: counting never uses it.
        at.Write(Path.Combine(chrome.Root, "Local State"), "{}");
        var preview = chrome.Preview("Default");
        Assert.Equal((3, 2, 6, 1), (preview.Bookmarks, preview.Places, preview.Passwords, preview.AppBound));
        Assert.Equal([StoreId], preview.Extensions);
    }

    [Fact]
    public void IconsComeForThePageOrElseItsSitesFrontDoor()
    {
        using var at = new ImportFixture();
        var (chrome, _) = MakeChrome(at);
        var icons = ImportFixture.Db(Path.Combine(chrome.Root, "Default", "Favicons"), "CREATE TABLE icon_mapping (page_url TEXT, icon_id INTEGER)", "CREATE TABLE favicon_bitmaps (icon_id INTEGER, image_data BLOB, width INTEGER)");
        ImportFixture.Run(icons, "INSERT INTO icon_mapping VALUES ('https://www.example.com/', 1)");
        ImportFixture.Run(icons, "INSERT INTO icon_mapping VALUES ('https://tiny.example/', 2)");
        ImportFixture.Run(icons, "INSERT INTO favicon_bitmaps VALUES (1, ?, 16)", new byte[100]);
        ImportFixture.Run(icons, "INSERT INTO favicon_bitmaps VALUES (1, ?, 32)", Enumerable.Repeat((byte)7, 200).ToArray());
        ImportFixture.Run(icons, "INSERT INTO favicon_bitmaps VALUES (2, ?, 32)", new byte[10]);
        var found = chrome.Icons([new Uri("https://www.example.com/deep/page"), new Uri("https://tiny.example/")], "Default");
        Assert.Equal(["example.com"], found.Keys);
        Assert.Equal(200, found["example.com"].Length);
    }

    [Fact]
    public void AnEmptyDataFolderIsSaidByName()
    {
        using var at = new ImportFixture();
        Directory.CreateDirectory(at.Path("Local", "Microsoft", "Edge", "User Data"));
        var said = Assert.Single(ImportSource.Unreadable(at.Roots));
        Assert.Equal("Edge", said.Source.Name);
        Assert.Contains(@"%LOCALAPPDATA%\Microsoft\Edge\User Data", said.Message);
    }

    [Fact]
    public void OperaKeepsItsOneProfileInItsOwnFolder()
    {
        using var at = new ImportFixture();
        at.Write(at.Path("Roaming", "Opera Software", "Opera Stable", "Bookmarks"), """{"roots":{"bookmark_bar":{"children":[{"type":"url","name":"O","url":"https://opera.example/"}]}}}""");
        var opera = ImportSource.Installed(at.Roots).Single();
        Assert.Equal([("Opera Stable", "Opera")], opera.Profiles.Select(p => (p.Id, p.Name)));
        Assert.Single(opera.Bookmarks().Nodes);
    }

    [Fact]
    public void ArcIsFoundInItsPackage()
    {
        using var at = new ImportFixture();
        at.Write(Path.Combine(at.ArcFolder, "User Data", "Default", "Bookmarks"), """{"roots":{}}""");
        var arc = (ChromiumSource)ImportSource.Installed(at.Roots).Single();
        Assert.True(arc.IsArc);
        Assert.Equal(Path.Combine(at.ArcFolder, "StorableSidebar.json"), arc.SidebarFile);
        Assert.True(arc.SessionRepeatsSpaces);
        Assert.Equal(Path.Combine(at.Folder, "Elsewhere", "User Data"), ((ChromiumSource)ImportSource.Known(at.Roots with { Arc = at.Path("Elsewhere") }).First(s => s.Name == "Arc")).Root);
    }
}

public class ImportSessionTests
{
    [Fact]
    public void ReadsANormalWindowsTabsInTheirOrder()
    {
        var data = ImportFixture.Snss(
            ImportFixture.Window(1, 10), ImportFixture.Window(1, 11), ImportFixture.Window(1, 12),
            ImportFixture.Index(10, 2), ImportFixture.Index(11, 0), ImportFixture.Index(12, 1),
            ImportFixture.Navigation(10, 0, "https://first.example/", "First"),
            ImportFixture.Navigation(11, 0, "https://a.example/", "A"), ImportFixture.Navigation(11, 1, "https://b.example/", "B"), ImportFixture.Selected(11, 0),
            ImportFixture.Navigation(12, 0, "https://pin.example/", ""), ImportFixture.Pinned(12, true),
            // A popup's tab, and a tab closed: neither comes.
            ImportFixture.Window(2, 20), ImportFixture.WindowType(2, 1), ImportFixture.Navigation(20, 0, "https://popup.example/", "Popup"),
            ImportFixture.Window(1, 13), ImportFixture.Navigation(13, 0, "https://gone.example/", "Gone"), ImportFixture.Closed(13));
        var snapshot = SessionImport.Read(data);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal([("https://a.example/", "A", false), ("https://pin.example/", "pin.example", true), ("https://first.example/", "First", false)], window.Select(t => (t.Url.AbsoluteUri, t.Title, t.Pinned)));
        Assert.Equal(3, snapshot.Found);
    }

    [Fact]
    public void GroupsComeByNameAndFoldedAsTheyWere()
    {
        var data = ImportFixture.Snss(
            ImportFixture.Window(1, 1), ImportFixture.Window(1, 2), ImportFixture.Window(1, 3),
            ImportFixture.Navigation(1, 0, "https://one.example/", "One"), ImportFixture.Navigation(2, 0, "https://two.example/", "Two"), ImportFixture.Navigation(3, 0, "https://three.example/", "Three"),
            ImportFixture.Group(1, 7, 8), ImportFixture.Group(2, 7, 8), ImportFixture.Group(3, 9, 9),
            ImportFixture.GroupMeta(7, 8, "Research", collapsed: true));
        var snapshot = SessionImport.Read(data);
        var tabs = snapshot.Windows.Single();
        Assert.Equal(tabs[0].Group, tabs[1].Group);
        Assert.Equal(new ImportedGroup("Research", true), snapshot.Groups[tabs[0].Group!.Value]);
        Assert.Equal(new ImportedGroup("Group 1", false), snapshot.Groups[tabs[2].Group!.Value]);
    }

    [Fact]
    public void APrunedTabKeepsTheEntryItWasOn()
    {
        var data = ImportFixture.Snss(ImportFixture.Window(1, 1),
            ImportFixture.Navigation(1, 0, "https://a.example/", "A"), ImportFixture.Navigation(1, 1, "https://b.example/", "B"), ImportFixture.Navigation(1, 2, "https://c.example/", "C"),
            ImportFixture.Selected(1, 2), (24, ImportFixture.Join(ImportFixture.I32(1), ImportFixture.I32(0), ImportFixture.I32(2))));
        Assert.Equal("https://c.example/", SessionImport.Read(data).Windows.Single().Single().Url.AbsoluteUri);
    }

    [Fact]
    public void ATornEndAndAnythingNotSnssBringNothingBad()
    {
        var data = ImportFixture.Snss(ImportFixture.Window(1, 1), ImportFixture.Navigation(1, 0, "https://a.example/", "A"));
        Assert.Single(SessionImport.Read([.. data, 0x40, 0x00, 6, 1, 2]).Windows);
        Assert.Empty(SessionImport.Read("SNSX\u0001\0\0\0"u8.ToArray()).Windows);
        Assert.Empty(SessionImport.Read([.. "SNSS"u8.ToArray(), 2, 0, 0, 0]).Windows);
        Assert.Empty(SessionImport.Read([]).Windows);
    }

    [Fact]
    public void CappedKeepsTheFirstTabsAndTheirGroupsOnly()
    {
        var a = new GroupToken(1, 1); var b = new GroupToken(2, 2);
        var snapshot = new SessionSnapshot { Windows = [[new(new Uri("https://1.example/"), "1", false, a), new(new Uri("https://2.example/"), "2", false, b)], [new(new Uri("https://3.example/"), "3", false)]], Groups = { [a] = new("A", false), [b] = new("B", false) } };
        var capped = snapshot.Capped(1);
        Assert.Equal(1, capped.Tabs);
        Assert.Equal(3, capped.Found);
        Assert.Equal([a], capped.Groups.Keys);
    }

    [Fact]
    public void TheNewestSessionFileOfAProfileIsRead()
    {
        using var at = new ImportFixture();
        var root = at.Path("Local", "BraveSoftware", "Brave-Browser", "User Data");
        at.Write(Path.Combine(root, "Default", "History"), "");
        var old = at.Write(Path.Combine(root, "Default", "Sessions", "Session_1"), ImportFixture.Snss(ImportFixture.Window(1, 1), ImportFixture.Navigation(1, 0, "https://old.example/", "Old")));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        at.Write(Path.Combine(root, "Default", "Sessions", "Session_2"), ImportFixture.Snss(ImportFixture.Window(1, 1), ImportFixture.Navigation(1, 0, "https://new.example/", "New")));
        var brave = ImportSource.Installed(at.Roots).Single();
        Assert.Equal("https://new.example/", brave.Sessions().Windows.Single().Single().Url.AbsoluteUri);
        Assert.Equal(1, brave.Preview().Session.Tabs);
    }
}
