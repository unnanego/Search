using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class PasswordCsvTests
{
    [Fact]
    public void ReadsChromesExport()
    {
        var found = PasswordCsv.Read("name,url,username,password,note\r\nexample.com,https://www.example.com/login,ann,\"p,a\"\"ss\",\r\nplain,http://plain.example/,bob,pw,\"a note\nover two lines\"\r\napp,android://x@com.vendor.app/,eve,pw,\r\n");
        Assert.True(found.Recognised);
        Assert.Equal([("example.com", "ann", "p,a\"ss", false), ("plain.example", "bob", "pw", true)], found.Logins.Select(l => (l.Host, l.User, l.Password, l.Clear)));
        Assert.Equal(1, found.Skipped);
    }

    [Fact]
    public void ReadsFirefoxsExportWithWhenItWasLastUsed()
    {
        var found = PasswordCsv.Read("﻿\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\n\"https://mozilla.org\",\"ann\",\"secret\",,\"https://mozilla.org\",\"{abc}\",\"1700000000000\",\"1704067200000\",\"1700000000000\"\n");
        var login = Assert.Single(found.Logins);
        Assert.Equal(("mozilla.org", "ann", "secret"), (login.Host, login.User, login.Password));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), login.Used);
    }

    [Theory]
    [InlineData("login_uri,login_username,login_password\nhttps://a.example,ann,pw\n")]
    [InlineData("Title;URL;Username;Password;Notes\nA;https://a.example;ann;pw;\n")]
    [InlineData("Web Site\tLogin Name\tPassword\nhttps://a.example\tann\tpw\n")]
    [InlineData("URL, Username, Password\r\n https://a.example , \"ann\" ,pw\r\n")]
    public void ColumnsAreFoundHoweverTheyAreSpelled(string text)
    {
        var login = Assert.Single(PasswordCsv.Read(text).Logins);
        Assert.Equal(("a.example", "pw"), (login.Host, login.Password));
    }

    [Fact]
    public void AFileWithoutAnAddressOrAPasswordIsNotOne()
    {
        var found = PasswordCsv.Read("first,last\nann,lee\n");
        Assert.False(found.Recognised);
        Assert.Empty(found.Logins);
        Assert.Equal(1, found.Skipped);
        Assert.False(PasswordCsv.Read("").Recognised);
    }

    [Fact]
    public void AStrayQuoteInsideAFieldIsKept()
    {
        var rows = PasswordCsv.Parse("a,b\"c,\"d\"e\",f\n");
        Assert.Equal(["a", "b\"c", "d\"e", "f"], rows.Single());
    }

    [Fact]
    public void TheSameLoginTwiceIsOne()
    {
        var found = PasswordCsv.Read("url,username,password\nhttps://a.example,ann,pw\nhttps://www.a.example/x,ann,pw\n");
        Assert.Single(found.Logins);
        Assert.Equal(1, found.Skipped);
    }

    [Fact]
    public void ReadsAFileThroughTheCheckedRead()
    {
        using var at = new ImportFixture();
        var file = at.Write(at.Path("Downloads", "Chrome Passwords.csv"), Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("name,url,username,password\nx,https://ü.example/,ann,pässword\n")).ToArray());
        var login = Assert.Single(PasswordCsv.ReadFile(file).Logins);
        Assert.Equal("pässword", login.Password);
        Assert.Equal(("ann", "pässword"), (login.ToImported().User, login.ToImported().Password));
    }
}

public class ImportRecordsTests
{
    [Fact]
    public void NotesAddUpAndForgettingBookmarksKeepsTheRest()
    {
        var settings = new Dictionary<string, string>();
        var records = new ImportRecords(settings);
        var one = Guid.NewGuid();
        var then = new DateTimeOffset(2025, 9, 27, 12, 0, 0, TimeSpan.Zero);
        records.Note("Chrome", [one], bookmarks: 1, places: 10, now: then);
        records.Note("Chrome", passwords: 3, spaces: 2, pinned: 5, now: then.AddDays(1));
        var record = new ImportRecords(settings).Of("Chrome")!;
        Assert.Equal((then.AddDays(1), 1, 10, 3, 2, 5), (record.Date, record.Bookmarks, record.Places, record.Passwords, record.Spaces, record.Pinned));
        Assert.Equal([one], record.BookmarkIds);
        records.ForgetBookmarks("Chrome");
        Assert.Equal((0, 10), (records.Of("Chrome")!.Bookmarks, records.Of("Chrome")!.Places));
        Assert.Empty(records.Of("Chrome")!.BookmarkIds);
        Assert.Null(records.Of("Edge"));
    }

    [Fact]
    public void WrittenAsTheMacWritesIt()
    {
        var settings = new Dictionary<string, string>();
        new ImportRecords(settings).Note("Arc", [Guid.Parse("0E984725-C51C-4BF4-9960-E1C80E27ABA0")], bookmarks: 1, now: SwiftJson.ReferenceDate.AddSeconds(100));
        Assert.Equal("""{"Arc":{"date":100,"bookmarks":1,"places":0,"passwords":0,"bookmarkIDs":["0E984725-C51C-4BF4-9960-E1C80E27ABA0"]}}""", settings["import.records"]);
    }

    [Fact]
    public void AMacsRecordReads()
    {
        var settings = new Dictionary<string, string> { ["import.records"] = """{"Safari":{"date":785000000,"bookmarks":2,"places":3,"passwords":4,"bookmarkIDs":[],"spaces":1}}""" };
        var record = new ImportRecords(settings).Of("Safari")!;
        Assert.Equal((2, 3, 4, (int?)1, (int?)null), (record.Bookmarks, record.Places, record.Passwords, record.Spaces, record.Pinned));
    }
}
