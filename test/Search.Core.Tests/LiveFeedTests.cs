using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// Live folders' reading (LiveFeed.cs), as LiveFoldersTests.swift holds the Mac's: RSS and Atom read, a page's feed
/// found, what is typed made an address, a refetch merged, and the half-hourly clock. Feeds are strings; no network.
public class LiveFeedTests
{
    private const string Base = "https://blog.example/feeds/main.xml";

    private static LiveFeed.Read Read(string xml) => LiveFeed.Parse(Encoding.UTF8.GetBytes(xml), Base) ?? throw new Xunit.Sdk.XunitException("not a feed");
    private static FeedItem Item(string title, string link) => new(Guid.NewGuid(), title, link);

    // Reading.

    [Fact]
    public void RssReadsTitlesLinksAndTheFeedsName()
    {
        var read = Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <title>A Blog</title><link>https://blog.example/</link>
              <item><title>First post</title><link>https://blog.example/1</link></item>
              <item><title>Second post</title><link>https://blog.example/2</link></item>
            </channel></rss>
            """);
        Assert.Equal("A Blog", read.Title);
        Assert.Equal(Base, read.Feed);
        Assert.Equal(["First post", "Second post"], read.Items.Select(i => i.Title));
        Assert.Equal(["https://blog.example/1", "https://blog.example/2"], read.Items.Select(i => i.Link));
    }

    [Fact]
    public void CdataAndEntitiesReadAsText()
    {
        var read = Read("""
            <rss version="2.0"><channel><title>Tom &amp; Jerry&#8217;s</title>
              <item><title><![CDATA[Cats <b>&</b> dogs]]></title><link>https://x.example/a</link></item>
              <item><title>Fish &amp;amp; chips &#x2014; again</title><link>https://x.example/b</link></item>
              <item><title>  spread
                  over lines  </title><link>https://x.example/c</link></item>
            </channel></rss>
            """);
        Assert.Equal("Tom & Jerry’s", read.Title);
        Assert.Equal(["Cats <b>&</b> dogs", "Fish & chips — again", "spread over lines"], read.Items.Select(i => i.Title));
    }

    [Fact]
    public void APostWithNoTitleIsCalledWhereItGoesAndOneWithNoLinkIsLeftOut()
    {
        var read = Read("""
            <rss version="2.0"><channel><title>T</title>
              <item><link>https://x.example/notes/7</link></item>
              <item><title>Nowhere</title></item>
              <item><title>By guid</title><guid>https://x.example/g</guid></item>
              <item><title>Not a link</title><guid isPermaLink="false">tag:x,1</guid></item>
              <item><title>Empty</title><link></link></item>
            </channel></rss>
            """);
        Assert.Equal(["x.example/notes/7", "By guid"], read.Items.Select(i => i.Title));
    }

    [Fact]
    public void RelativeLinksAreReadAgainstTheFeedAndOnlyWebOnesKept()
    {
        var read = Read("""
            <rss version="2.0"><channel><title>T</title>
              <item><title>Root</title><link>/posts/1</link></item>
              <item><title>Beside</title><link>two.html</link></item>
              <item><title>Script</title><link>javascript:alert(1)</link></item>
              <item><title>File</title><link>file:///C:/Windows/win.ini</link></item>
            </channel></rss>
            """);
        Assert.Equal(["https://blog.example/posts/1", "https://blog.example/feeds/two.html"], read.Items.Select(i => i.Link));
    }

    [Fact]
    public void AtomReadsTheAlternateLinkAndHtmlTitles()
    {
        var read = Read("""
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title type="text">Releases from owner/repo</title>
              <link rel="self" href="https://github.com/owner/repo/releases.atom"/>
              <entry>
                <title type="html">v1.2 &lt;em&gt;stable&lt;/em&gt;</title>
                <link rel="self" href="https://api.example/entries/1"/>
                <link rel="alternate" type="text/html" href="/owner/repo/releases/tag/v1.2"/>
                <source><title>Not this one</title></source>
              </entry>
              <entry><title>No rel</title><link href="https://x.example/norel"/></entry>
              <entry><title>Only self</title><link rel="self" href="https://x.example/self"/></entry>
            </feed>
            """);
        Assert.Equal("Releases from owner/repo", read.Title);
        Assert.Equal(["v1.2 stable", "No rel"], read.Items.Select(i => i.Title));
        Assert.Equal("https://blog.example/owner/repo/releases/tag/v1.2", read.Items[0].Link);
    }

    [Fact]
    public void Rss1IsAFeedToo()
    {
        var read = Read("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/">
              <channel><title>Old School</title></channel>
              <item><title>One</title><link>https://x.example/1</link></item>
            </rdf:RDF>
            """);
        Assert.Equal("Old School", read.Title);
        Assert.Equal(["One"], read.Items.Select(i => i.Title));
    }

    [Fact]
    public void APrefixNoOneDeclaredStillReads()
    {
        // The Mac's parser doesn't check namespaces; nor does this.
        var read = Read("""<rss version="2.0"><channel><title>T</title><item><title>A</title><media:thumbnail url="x"/><link>https://x.example/a</link></item></channel></rss>""");
        Assert.Equal(["A"], read.Items.Select(i => i.Title));
    }

    [Fact]
    public void AFeedWithNoNameIsCalledByItsSite() =>
        Assert.Equal("blog.example", Read("""<rss version="2.0"><channel><item><title>A</title><link>https://x.example/a</link></item></channel></rss>""").Title);

    [Fact]
    public void AtMostTwelveAndNoRepeats()
    {
        var items = string.Concat(Enumerable.Range(1, 30).Select(n => $"<item><title>P{n}</title><link>https://x.example/{(n == 2 ? 1 : n)}</link></item>"));
        var read = Read($"""<rss version="2.0"><channel><title>T</title>{items}</channel></rss>""");
        Assert.Equal(LiveFeed.Most, read.Items.Count);
        Assert.Equal(["P1", "P3"], read.Items.Take(2).Select(i => i.Title));
    }

    [Fact]
    public void ALongTitleIsCutAtThreeHundred()
    {
        var read = Read($"""<rss version="2.0"><channel><title>T</title><item><title>{new string('a', 400)}</title><link>https://x.example/a</link></item></channel></rss>""");
        Assert.Equal(300, read.Items[0].Title.Length);
    }

    [Fact]
    public void APageOrBrokenXmlIsNoFeedButKeepsWhatWasRead()
    {
        Assert.Null(LiveFeed.Parse("<!doctype html><html><head><title>x</title></head></html>"u8.ToArray(), Base));
        Assert.Null(LiveFeed.Parse("<html><head><title>x</title></head></html>"u8.ToArray(), Base));
        Assert.Null(LiveFeed.Parse("not xml at all"u8.ToArray(), Base));
        Assert.Null(LiveFeed.Parse([], Base));
        var broken = Read("""<rss version="2.0"><channel><title>T</title><item><title>Kept</title><link>https://x.example/k</link></item><item><title>Bad &nbsp; entity""");
        Assert.Equal(["Kept"], broken.Items.Select(i => i.Title));
    }

    [Fact]
    public void AnOldFeedsDoctypeIsPassedOverAndNothingOutsideIsRead()
    {
        var old = Read("""
            <?xml version="1.0"?>
            <!DOCTYPE rss PUBLIC "-//Netscape Communications//DTD RSS 0.91//EN" "http://my.netscape.com/publish/formats/rss-0.91.dtd">
            <rss version="0.91"><channel><title>Old</title><item><title>One</title><link>https://x.example/1</link></item></channel></rss>
            """);
        Assert.Equal(["One"], old.Items.Select(i => i.Title));
        var secret = Path.GetTempFileName();
        try
        {
            File.WriteAllText(secret, "SECRET");
            var uri = new Uri(secret).AbsoluteUri;
            var xxe = LiveFeed.Parse(Encoding.UTF8.GetBytes($"""
                <?xml version="1.0"?>
                <!DOCTYPE rss [<!ENTITY x SYSTEM "{uri}"><!ENTITY lol "lol">]>
                <rss version="2.0"><channel><title>T</title><item><title>Before</title><link>https://x.example/0</link></item><item><title>&x;&lol;</title><link>https://x.example/1</link></item></channel></rss>
                """), Base);
            Assert.NotNull(xxe);
            Assert.DoesNotContain(xxe.Items, i => i.Title.Contains("SECRET") || i.Title.Contains("lol"));
            Assert.Equal(["Before"], xxe.Items.Select(i => i.Title));
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public void AnEncodingTheXmlNamesIsRead()
    {
        var latin = Encoding.Latin1.GetBytes("""<?xml version="1.0" encoding="ISO-8859-1"?><rss version="2.0"><channel><title>Café</title><item><title>Crème</title><link>https://x.example/c</link></item></channel></rss>""");
        var read = LiveFeed.Parse(latin, Base)!;
        Assert.Equal("Café", read.Title);
        Assert.Equal("Crème", read.Items[0].Title);
    }

    [Fact]
    public void EntitiesItDoesntKnowAreLeft()
    {
        Assert.Equal("a & b … &bogus; &#xD800; 😀", LiveFeed.Decode("a &amp; b &hellip; &bogus; &#xD800; &#128512;"));
        Assert.Equal("x y", LiveFeed.Clean("<p>x</p>\u00A0 <br/>y", html: true));
    }

    // Finding a page's feed.

    [Fact]
    public void APagesFeedIsFoundFromItsAlternateLink()
    {
        const string page = "https://site.example/blog/";
        var html = """
            <!doctype html><html><head>
            <link rel="stylesheet" href="/s.css">
            <link rel="alternate" type="application/json" href="/feed.json">
            <LINK REL='alternate' TYPE='application/atom+xml' TITLE='Atom' HREF='atom.xml?a=1&amp;b=2'>
            <link rel="alternate" type="application/rss+xml" href="/rss.xml">
            </head></html>
            """;
        Assert.Equal("https://site.example/blog/atom.xml?a=1&b=2", LiveFeed.Discover(Encoding.UTF8.GetBytes(html), page));
        Assert.Null(LiveFeed.Discover("<html><link rel=alternate type=application/rss+xml href=\"javascript:x\"></html>"u8.ToArray(), page));
        Assert.Null(LiveFeed.Discover("<html><head></head></html>"u8.ToArray(), page));
        Assert.Equal("https://site.example/rss", LiveFeed.Discover("<link rel=\"alternate home\" type=\"application/rss+xml\" href=\"/rss\">"u8.ToArray(), page));
    }

    [Fact]
    public void WhatIsTypedIsAWebAddressOrNothing()
    {
        Assert.Equal("https://example.com/feed", LiveFeed.Address("example.com/feed"));
        Assert.Equal("http://example.com/rss", LiveFeed.Address("  http://example.com/rss  "));
        Assert.Null(LiveFeed.Address("file:///etc/passwd"));
        Assert.Null(LiveFeed.Address("javascript:alert(1)"));
        Assert.Null(LiveFeed.Address("two words"));
        Assert.Null(LiveFeed.Address(""));
        Assert.Null(LiveFeed.Address("ftp://example.com/feed"));
    }

    [Fact]
    public void WhyNoFolderIsSaidInASentence()
    {
        Assert.Equal("blog.example couldn’t be reached.", LiveFeed.Said(LiveFeed.Problem.Network, Base));
        Assert.Equal("What blog.example sent is too large to be a feed.", LiveFeed.Said(LiveFeed.Problem.TooBig, Base));
        Assert.Equal($"There’s no RSS or Atom feed at {Base}, nor one that page points to.", LiveFeed.Said(LiveFeed.Problem.NotAFeed, Base));
    }

    // The folder.

    [Fact]
    public void ARefetchKeepsEachPostsIdByLink()
    {
        var old = new[] { Item("A", "https://x.example/a") };
        var fresh = new[] { Item("B", "https://x.example/b"), Item("A", "https://x.example/a") };
        var kept = LiveFeed.KeepingIds(fresh, old);
        Assert.Equal(old[0], kept[1]);
        Assert.Equal(fresh[0], kept[0]);
    }

    private static PinFolder Live(string name = "Feed", Guid? parent = null) =>
        new(Guid.NewGuid(), name) { Parent = parent, Feed = "https://x.example/feed", Items = [Item("Post", "https://x.example/p")] };

    [Fact]
    public void AFetchReplacesThePostsAndOnlyThem()
    {
        var work = new PinFolder(Guid.NewGuid(), "Work");
        var live = Live("Renamed meanwhile") with { Collapsed = true, Parent = work.Id };
        var read = new LiveFeed.Read("Feed's own name", "https://elsewhere.example/feed", [Item("New", "https://x.example/n"), Item("Post again", "https://x.example/p")]);
        var (merged, error) = LiveFeed.Merged(live, LiveFeed.Fetched.Ok(read));
        Assert.Null(error);
        Assert.Equal(live with { Items = merged.Items }, merged);
        Assert.Equal(["New", "Post again"], merged.Items!.Select(i => i.Title));
        Assert.Equal(live.Items![0].Id, merged.Items![1].Id);
        // Unchanged, it is equal: nothing written.
        var again = LiveFeed.Merged(merged, LiveFeed.Fetched.Ok(read with { Items = [Item("New", "https://x.example/n"), Item("Post again", "https://x.example/p")] })).Folder;
        Assert.Equal(merged, again);
    }

    [Fact]
    public void AFailedFetchKeepsThePostsAndSaysSo()
    {
        var live = Live();
        var (merged, error) = LiveFeed.Merged(live, LiveFeed.Fetched.Failed(LiveFeed.Problem.Network));
        Assert.Same(live, merged);
        Assert.Equal(LiveFeed.CouldNotRefresh, error);
        Assert.Equal(LiveFeed.CouldNotRefresh, LiveFeed.Tip(live, failed: true));
        Assert.Equal("x.example", LiveFeed.Tip(live, failed: false));
        Assert.Equal("", LiveFeed.Tip(new PinFolder(Guid.NewGuid(), "Plain"), failed: true));
    }

    [Fact]
    public void ARefreshOfAFolderGoneOrPlainChangesNothing()
    {
        var live = Live();
        var plain = new PinFolder(Guid.NewGuid(), "Work");
        var ok = LiveFeed.Fetched.Ok(new LiveFeed.Read("T", live.Feed!, []));
        Assert.Null(LiveFeed.Refreshed([plain], live.Id, ok));
        Assert.Null(LiveFeed.Refreshed([plain], plain.Id, ok));
        var (folders, error) = LiveFeed.Refreshed([plain, live], live.Id, ok)!.Value;
        Assert.Null(error);
        Assert.Equal([plain, live with { Items = [] }], folders);
    }

    [Fact]
    public void ANewLiveFolderIsNamedByItsFeedAndGoesWhereOneMay()
    {
        var work = new PinFolder(Guid.NewGuid(), "Work");
        var other = Live("Other");
        var read = new LiveFeed.Read(new string('n', 250), "https://x.example/feed", [Item("A", "https://x.example/a")]);
        var (folders, made) = LiveFeed.Made([work, other], read, work.Id);
        var folder = folders.Single(f => f.Id == made);
        Assert.Equal(200, folder.Name.Length);
        Assert.Equal(work.Id, folder.Parent);
        Assert.True(folder.IsLive);
        Assert.Equal(read.Items, folder.Items);
        // Inside a live one, or a folder that isn't there: at the top.
        Assert.Null(LiveFeed.Made([work, other], read, other.Id).Folders[^1].Parent);
        Assert.Null(LiveFeed.Made([work], read, Guid.NewGuid()).Folders[^1].Parent);
    }

    // The clock.

    [Fact]
    public void EveryFolderIsFetchedALittleAfterLaunchThenEveryHalfHour()
    {
        var now = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        var start = now;
        var schedule = new LiveSchedule(() => now);
        Assert.False(schedule.Due(true));
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.Wait);
        now += TimeSpan.FromSeconds(5);
        Assert.True(schedule.Due(true));
        Assert.False(schedule.Due(true));
        Assert.Equal(start + TimeSpan.FromMinutes(30), schedule.Next);
        now = start + TimeSpan.FromMinutes(30);
        Assert.True(schedule.Due(true));
        Assert.Equal(start + TimeSpan.FromMinutes(60), schedule.Next);
        // Asleep for hours: one fetch on waking, the next on the half-hour after.
        now = start + TimeSpan.FromMinutes(200);
        Assert.True(schedule.Due(true));
        Assert.False(schedule.Due(true));
        Assert.Equal(start + TimeSpan.FromMinutes(210), schedule.Next);
    }

    [Fact]
    public void WithTheSwitchOffNothingIsDueAndTheTimePasses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var schedule = new LiveSchedule(() => now);
        now += TimeSpan.FromSeconds(6);
        Assert.False(schedule.Due(false));
        // Turned on later: the next half hour, not at once.
        Assert.False(schedule.Due(true));
        now = DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(30);
        Assert.True(schedule.Due(true));
    }
}
