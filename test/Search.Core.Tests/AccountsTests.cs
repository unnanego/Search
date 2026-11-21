using Search.Core;

namespace Search.Core.Tests;

public class AccountsTests
{
    private static readonly HashSet<string> _list = ["com", "uk", "co.uk", "github.io"];
    private static bool Suffix(string name) => _list.Contains(name);
    private static readonly DateTimeOffset _day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static Account A(string host, string user, int? usedDay = null, bool clear = false) => new(host, user, _day, usedDay is { } d ? _day.AddDays(d) : null, clear);
    private static string Names(IEnumerable<Account> list) => string.Join(" ", list.Select(a => $"{a.Host}/{a.User}"));

    [Fact] public void PageHostIsLowercaseWithoutWww() => Assert.Equal("example.com", Accounts.Host(new Uri("https://WWW.Example.com/login")));
    [Fact] public void OnlyWebsitesHaveAHost() { Assert.Null(Accounts.Host(new Uri("file:///C:/x.html"))); Assert.Null(Accounts.Host(new Uri("about:blank"))); Assert.Null(Accounts.Host((Uri?)null)); }
    [Fact] public void TypedSiteGetsAScheme() => Assert.Equal("accounts.example.com", Accounts.Host(" accounts.example.com/x "));
    [Fact] public void AppLoginsInAnExportAreNoSite() => Assert.Equal("", Accounts.Host("android://hash@com.vendor.app/"));

    [Fact] public void ExactHostFirstThenSameSiteEachNewestFirst()
    {
        var kept = new[] { A("example.com", "old", 1), A("accounts.example.com", "a", 2), A("example.com", "new", 5), A("other.com", "x", 9), A("shop.example.com", "b", 7) };
        Assert.Equal("example.com/new example.com/old shop.example.com/b accounts.example.com/a", Names(Accounts.Matching(kept, "example.com", true, Suffix)));
    }

    [Fact] public void NeverUsedComesLast() => Assert.Equal("example.com/used example.com/never", Names(Accounts.Matching([A("example.com", "never"), A("example.com", "used", 1)], "example.com", true, Suffix)));
    [Fact] public void NotWideMeansThisHostAlone() => Assert.Equal("console.example.com/me", Names(Accounts.Matching([A("example.com", "gmail"), A("console.example.com", "me")], "console.example.com", false, Suffix)));
    [Fact] public void WithoutSuffixListOnlyTheExactHost() => Assert.Equal("a.example.com/me", Names(Accounts.Matching([A("example.com", "x"), A("a.example.com", "me")], "a.example.com", true, null)));
    [Fact] public void NeighboursOnAPrivateSuffixAreNotOneSite() => Assert.Empty(Accounts.Matching([A("alice.github.io", "alice")], "bob.github.io", true, Suffix));

    [Fact] public void PlainHttpPageGetsOnlyWhatWasKeptOverHttp() =>
        Assert.Equal("example.com/clear", Names(Accounts.Offered([A("example.com", "secure", 3), A("example.com", "clear", 1, clear: true)], "example.com", true, true, null, Suffix)));

    [Fact] public void HttpsPageGetsBoth() => Assert.Equal(2, Accounts.Offered([A("example.com", "secure", 3), A("example.com", "clear", 1, clear: true)], "example.com", true, false, null, Suffix).Count);

    [Fact] public void GivenNameComesFirst() =>
        Assert.Equal("example.com/Me@Mail.com example.com/new", Names(Accounts.Offered([A("example.com", "new", 5), A("example.com", "Me@Mail.com", 1)], "example.com", true, false, "me@mail.com", Suffix)));

    [Fact] public void AtMostFive() => Assert.Equal(Accounts.Most, Accounts.Offered(Enumerable.Range(0, 9).Select(i => A("example.com", "u" + i, i)), "example.com", true, false, null, Suffix).Count);

    [Fact] public void SplitPutsTheSitesOthersUnderTheHostsOwn()
    {
        var (own, others) = Accounts.Split([A("shop.example.com", "b"), A("example.com", "a")], "example.com");
        Assert.Equal("example.com/a", Names(own));
        Assert.Equal("shop.example.com/b", Names(others));
    }

    [Fact] public void SamePasswordIsKnown() => Assert.Equal(Saving.Known, Accounts.Judge([A("example.com", "me")], "example.com", "me", "pw", _ => "pw"));
    [Fact] public void NewPasswordForAKeptAccountIsAnUpdate() => Assert.Equal(Saving.Changed, Accounts.Judge([A("example.com", "me")], "example.com", "me", "pw2", _ => "pw"));
    [Fact] public void AnotherNameIsNew() => Assert.Equal(Saving.New, Accounts.Judge([A("example.com", "me")], "example.com", "you", "pw", _ => "pw"));
    [Fact] public void AnotherHostIsNew() => Assert.Equal(Saving.New, Accounts.Judge([A("example.com", "me")], "accounts.example.com", "me", "pw", _ => "pw"));

    [Fact]
    public void OnlyTheMatchingAccountsSecretIsRead()
    {
        var read = new List<string>();
        Accounts.Judge([A("example.com", "a"), A("example.com", "me"), A("example.com", "b")], "example.com", "me", "pw", a => { read.Add(a.User); return "pw"; });
        Assert.Equal(["me"], read);
    }

    [Fact] public void NeverByHost() => Assert.True(Accounts.IsNever(new HashSet<string> { "example.com" }, "example.com"));
    [Fact] public void NeverDoesntSpillToOtherSites() => Assert.False(Accounts.IsNever(new HashSet<string> { "example.com" }, "other.com"));

    [Fact]
    public void PanelGroupsBySiteAndFilters()
    {
        var kept = new[] { A("b.com", "zed"), A("a.com", "Bob"), A("a.com", "al"), A("c.com", "carol") };
        Assert.Equal(["a.com", "b.com", "c.com"], Accounts.Sites(kept).Select(s => s.Host));
        Assert.Equal(["Bob", "al"], Accounts.Sites(kept)[0].Accounts.Select(a => a.User));
        Assert.Equal(["a.com"], Accounts.Sites(kept, " bob ").Select(s => s.Host));
        Assert.Equal(["c.com"], Accounts.Sites(kept, "c.c").Select(s => s.Host));
    }

    [Fact] public void SortedBySiteThenName() => Assert.Equal("a.com/a a.com/b b.com/a", Names(Accounts.Sorted([A("b.com", "a"), A("a.com", "b"), A("a.com", "a")])));

    [Fact]
    public void CsvWithQuotesCommasAndNewlines()
    {
        var csv = "name,url,username,password,note\r\nEx,https://www.example.com/login,me,\"p,w\"\"d\nx\",\r\nApp,android://h@com.vendor.app/,me,pw,\r\nOld,http://old.example.com,you,pw2,\r\nEmpty,https://e.com,me,,\r\n";
        var (rows, skipped) = Accounts.FromCsv(csv);
        Assert.Equal(2, skipped);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Imported("example.com", "me", "p,w\"d\nx", false), rows[0]);
        Assert.Equal(new Imported("old.example.com", "you", "pw2", true), rows[1]);
    }

    [Fact] public void CsvWithoutTheColumnsKeepsNothing() => Assert.Equal(([], 1), (Accounts.FromCsv("a,b\n1,2\n").Rows.ToArray(), Accounts.FromCsv("a,b\n1,2\n").Skipped));
    [Fact] public void ExportedRowsNeverPrintTheirPassword() => Assert.DoesNotContain("hunter2", new Imported("example.com", "me", "hunter2", false).ToString());
    [Fact] public void AccountIdIsHostAndName() => Assert.Equal("example.com\u0001me", A("example.com", "me").Id);
}
