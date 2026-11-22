using System.Security.Cryptography;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class ImportGeckoTests
{
    /// 2024-01-01 in Firefox's microseconds since 1970.
    private const long Then = 1704067200000000;

    /// Firefox with two profiles: "abc.default-release" (the one its install opens) and "xyz.old".
    private static (GeckoSource Firefox, string Profile) MakeFirefox(ImportFixture at)
    {
        var root = at.Path("Roaming", "Mozilla", "Firefox");
        at.Write(Path.Combine(root, "profiles.ini"), "[Profile1]\r\nName=old\r\nIsRelative=1\r\nPath=Profiles/xyz.old\r\nDefault=1\r\n\r\n[Profile0]\r\nName=default-release\r\nIsRelative=1\r\nPath=Profiles/abc.default-release\r\n\r\n[Install308046B0AF4A39CB]\r\nDefault=Profiles/abc.default-release\r\nLocked=1\r\n");
        var profile = Path.Combine(root, "Profiles", "abc.default-release");
        var places = ImportFixture.Db(Path.Combine(profile, "places.sqlite"),
            "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER DEFAULT 0, hidden INTEGER DEFAULT 0, last_visit_date INTEGER)",
            "CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER DEFAULT NULL, parent INTEGER, position INTEGER, title LONGVARCHAR, guid TEXT)",
            "INSERT INTO moz_places VALUES (1, 'https://mozilla.org/', 'Mozilla', 5, 0, 1704067200000000)",
            "INSERT INTO moz_places VALUES (2, 'https://mdn.example/', 'MDN', 2, 0, 1703980800000000)",
            "INSERT INTO moz_places VALUES (3, 'place:sort=8', 'Query', 0, 0, NULL)",
            "INSERT INTO moz_places VALUES (4, 'https://tagged.example/', 'Tagged', 1, 1, 1704067200000000)",
            "INSERT INTO moz_places VALUES (5, 'https://phone.example/', 'Phone', 0, 0, NULL)",
            "INSERT INTO moz_bookmarks VALUES (1, 2, NULL, 0, 0, '', 'root________')",
            "INSERT INTO moz_bookmarks VALUES (2, 2, NULL, 1, 0, 'menu', 'menu________')",
            "INSERT INTO moz_bookmarks VALUES (3, 2, NULL, 1, 1, 'toolbar', 'toolbar_____')",
            "INSERT INTO moz_bookmarks VALUES (4, 2, NULL, 1, 2, 'tags', 'tags________')",
            "INSERT INTO moz_bookmarks VALUES (5, 2, NULL, 1, 3, 'unfiled', 'unfiled_____')",
            "INSERT INTO moz_bookmarks VALUES (6, 2, NULL, 1, 4, 'mobile', 'mobile______')",
            "INSERT INTO moz_bookmarks VALUES (10, 1, 1, 3, 0, 'Mozilla', 'a1')",
            "INSERT INTO moz_bookmarks VALUES (11, 2, NULL, 3, 1, 'Dev', 'a2')",
            "INSERT INTO moz_bookmarks VALUES (12, 1, 2, 11, 0, 'MDN', 'a3')",
            "INSERT INTO moz_bookmarks VALUES (13, 1, 3, 3, 2, 'Query', 'a4')",
            "INSERT INTO moz_bookmarks VALUES (14, 1, 2, 2, 0, 'From the menu', 'a5')",
            "INSERT INTO moz_bookmarks VALUES (15, 2, NULL, 4, 0, 'a tag', 'a6')",
            "INSERT INTO moz_bookmarks VALUES (16, 1, 4, 15, 0, 'Tagged', 'a7')",
            "INSERT INTO moz_bookmarks VALUES (17, 1, 5, 6, 0, 'Phone', 'a8')");
        _ = places;
        ImportFixture.Db(Path.Combine(root, "Profiles", "xyz.old", "places.sqlite"), "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url LONGVARCHAR, title LONGVARCHAR, visit_count INTEGER, hidden INTEGER, last_visit_date INTEGER)");
        File.SetLastWriteTimeUtc(Path.Combine(root, "Profiles", "xyz.old", "places.sqlite"), DateTime.UtcNow.AddDays(-30));
        return ((GeckoSource)ImportSource.Known(at.Roots).First(s => s.Name == "Firefox"), profile);
    }

    [Fact]
    public void ProfilesComeFromProfilesIniAndTheInstallsDefaultLeads()
    {
        using var at = new ImportFixture();
        var (firefox, _) = MakeFirefox(at);
        Assert.Equal([("abc.default-release", "default-release"), ("xyz.old", "old")], firefox.Profiles.Select(p => (p.Id, p.Name)));
        Assert.Equal("abc.default-release", firefox.Usual);
    }

    [Fact]
    public void BookmarksAreRebuiltToolbarFirstAndTagsLeftOut()
    {
        using var at = new ImportFixture();
        var (firefox, _) = MakeFirefox(at);
        var read = firefox.Bookmarks("abc.default-release");
        Assert.True(read.Complete);
        Assert.Equal(["Mozilla", "Dev", "From the menu", "Mobile"], read.Nodes.Select(n => n.Title));
        Assert.Equal("https://mdn.example/", read.Nodes[1].Children!.Single().Url);
        Assert.Equal("https://phone.example/", read.Nodes[3].Children!.Single().Url);
        // The old profile's places.sqlite has no bookmarks table: it didn't read.
        Assert.False(firefox.Bookmarks().Complete);
    }

    [Fact]
    public void HistoryIsInMicrosecondsSince1970()
    {
        using var at = new ImportFixture();
        var (firefox, _) = MakeFirefox(at);
        var places = firefox.Places();
        Assert.Equal(["https://mozilla.org/", "https://mdn.example/"], places.Select(p => p.Url.AbsoluteUri));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), places[0].Last);
        Assert.Equal(5, places[0].Count);
    }

    [Fact]
    public void OpenTabsAndGroupsComeFromTheNewestSessionStore()
    {
        using var at = new ImportFixture();
        var (firefox, profile) = MakeFirefox(at);
        at.Write(Path.Combine(profile, "sessionstore-backups", "recovery.jsonlz4"), ImportFixture.JsonLz4("""
            {"windows":[
              {"tabs":[
                {"entries":[{"url":"https://a.example/","title":"A"},{"url":"https://b.example/","title":"B"}],"index":1},
                {"entries":[{"url":"https://grouped.example/","title":"In a group"}],"groupId":"g1"},
                {"entries":[{"url":"https://pin.example/","title":"Pin"}],"pinned":true},
                {"entries":[{"url":"about:newtab"}]}],
               "groups":[{"id":"g1","name":"Research","collapsed":true}]},
              {"isPopup":true,"tabs":[{"entries":[{"url":"https://popup.example/"}]}]}]}
            """));
        var old = at.Write(Path.Combine(profile, "sessionstore.jsonlz4"), ImportFixture.JsonLz4("""{"windows":[{"tabs":[{"entries":[{"url":"https://stale.example/"}]}]}]}"""));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var snapshot = firefox.Sessions("abc.default-release");
        var tabs = snapshot.Windows.Single();
        Assert.Equal([("https://a.example/", "A", false), ("https://grouped.example/", "In a group", false), ("https://pin.example/", "Pin", true)], tabs.Select(t => (t.Url.AbsoluteUri, t.Title, t.Pinned)));
        Assert.Equal(new ImportedGroup("Research", true), snapshot.Groups[tabs[1].Group!.Value]);
        Assert.Equal(3, firefox.Preview("abc.default-release").Session.Tabs);
    }

    [Fact]
    public void ZensOpenTabsLeaveItsPinsBehind()
    {
        var top = System.Text.Json.JsonDocument.Parse("""{"windows":[{"tabs":[{"entries":[{"url":"https://a.example/"}]},{"pinned":true,"entries":[{"url":"https://p.example/"}]},{"zenEssential":"true","entries":[{"url":"https://e.example/"}]},{"zenIsGlance":true,"entries":[{"url":"https://g.example/"}]}]}]}""");
        Assert.Equal(["https://a.example/"], MozillaSession.Read(top.RootElement, leavingPins: true).Windows.Single().Select(t => t.Url.AbsoluteUri));
        Assert.Equal(3, MozillaSession.Read(top.RootElement, leavingPins: false).Tabs);
    }

    private static byte[] MakeLogins(string profile, string primary = "")
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var master = RandomNumberGenerator.GetBytes(32);
        var key4 = ImportFixture.Db(Path.Combine(profile, "key4.db"), "CREATE TABLE metaData (id PRIMARY KEY UNIQUE ON CONFLICT REPLACE, item1, item2)", "CREATE TABLE nssPrivate (id PRIMARY KEY UNIQUE ON CONFLICT ABORT, a11, a102)");
        ImportFixture.Run(key4, "INSERT INTO metaData VALUES ('password', ?, ?)", salt, ImportFixture.Pbes2("password-check"u8.ToArray(), salt, primary));
        ImportFixture.Run(key4, "INSERT INTO nssPrivate VALUES (1, ?, ?)", ImportFixture.Pbes2(new byte[16], salt, primary), new byte[16]);
        byte[] id = [0xf8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
        ImportFixture.Run(key4, "INSERT INTO nssPrivate VALUES (2, ?, ?)", ImportFixture.Pbes2(master, salt, primary), id);
        File.WriteAllText(Path.Combine(profile, "logins.json"), $$"""
            {"nextId":4,"logins":[
              {"id":1,"hostname":"https://www.example.com","encryptedUsername":"{{ImportFixture.LoginField("ann", master)}}","encryptedPassword":"{{ImportFixture.LoginField("hunter2", master)}}","timeLastUsed":1704067200000},
              {"id":2,"hostname":"http://plain.example","encryptedUsername":"{{ImportFixture.LoginField("", master)}}","encryptedPassword":"{{ImportFixture.LoginField("pw", master)}}"},
              {"id":3,"hostname":"chrome://FirefoxAccounts","encryptedUsername":"{{ImportFixture.LoginField("x", master)}}","encryptedPassword":"{{ImportFixture.LoginField("y", master)}}"},
              {"id":4,"hostname":"https://broken.example","encryptedUsername":"AAAA","encryptedPassword":"{{ImportFixture.LoginField("z", master)}}"}],
             "disabledHosts":["https://never.example"]}
            """);
        return master;
    }

    [Fact]
    public void PasswordsDecryptWithTheEmptyPrimaryPassword()
    {
        using var at = new ImportFixture();
        var (firefox, profile) = MakeFirefox(at);
        MakeLogins(profile);
        var found = firefox.Passwords("abc.default-release");
        Assert.Equal(LoginTrouble.None, found.Trouble);
        Assert.Equal([("example.com", "ann", "hunter2", false), ("plain.example", "", "pw", true)], found.Logins.Select(l => (l.Host, l.User, l.Password, l.Clear)));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), found.Logins[0].Used);
        Assert.Equal(["never.example"], found.Never);
        Assert.Equal((1, 1), (found.Skipped, found.Undecrypted));
        Assert.Equal(4, firefox.Preview("abc.default-release").Passwords);
    }

    [Fact]
    public void APrimaryPasswordSaysToExport()
    {
        using var at = new ImportFixture();
        var (firefox, profile) = MakeFirefox(at);
        MakeLogins(profile, primary: "secret");
        var found = firefox.Passwords("abc.default-release");
        Assert.Equal(LoginTrouble.PrimaryPassword, found.Trouble);
        Assert.Contains("about:logins", found.Advice);
    }

    [Fact]
    public void AnOlderProfilesTripleDesFieldDecrypts()
    {
        var master = RandomNumberGenerator.GetBytes(24);
        var iv = RandomNumberGenerator.GetBytes(8);
        using var des = TripleDES.Create();
        des.Key = master;
        var sealedText = des.EncryptCbc(Encoding.UTF8.GetBytes("old-style"), iv, PaddingMode.PKCS7);
        var field = Convert.ToBase64String(ImportFixture.Seq(ImportFixture.Octets(new byte[16]), ImportFixture.Seq(ImportFixture.Oid("1.2.840.113549.3.7"), ImportFixture.Octets(iv)), ImportFixture.Octets(sealedText)));
        Assert.Equal("old-style", GeckoSource.DecryptLogin(field, [.. master, .. new byte[8]]));
    }

    [Fact]
    public void DamagedDerReadsAsNothing()
    {
        Assert.Null(Der.Read([0x30, 0x84, 0xff, 0xff, 0xff, 0xff], 0));
        Assert.Null(Der.Read([0x30, 0x05, 1], 0));
        Assert.Null(Der.Unwrap([0x30, 0x00], [1], []));
        Assert.Null(GeckoSource.DecryptLogin("not base64!", new byte[32]));
        Assert.Equal("1.2.840.113549.1.5.13", Der.Oid(ImportFixture.Oid("1.2.840.113549.1.5.13")[2..]));
    }
}
