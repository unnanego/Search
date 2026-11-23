using Search.Core;

namespace Search.Core.Tests;

/// A split pinned as a split (the Mac's PinSplitTests): Pin on one page pins every page, as rows naming one pair, and
/// they stay one split; Unpin lets both out, still a split; two pins put side by side become one.
public class PinSplitTests
{
    private static (Browser Browser, Tab[] Tabs) Row()
    {
        var browser = new Browser();
        var tabs = new[] { "one", "two", "three", "four" }.Select(w => browser.Open(new Uri($"https://{w}.example/"))).ToArray();
        foreach (var (tab, word) in tabs.Zip(new[] { "one", "two", "three", "four" })) tab.Title = word;
        return (browser, tabs);
    }

    private static string[] Titles(Browser browser) => browser.Tabs.Select(t => t.Title).ToArray();

    [Fact]
    public void APairIsPinnedTogetherAndStaysTogether()
    {
        var (browser, tabs) = Row();
        browser.Split(tabs[0], tabs[1]);
        Assert.NotNull(browser.PinnableSplit(tabs[0]));

        browser.Pin(tabs[0]);
        Assert.True(tabs[0].Pinned && tabs[1].Pinned);
        Assert.True(tabs[0].Listed && tabs[1].Listed, "pinned as rows, whatever was asked");
        Assert.Equal(new[] { tabs[0], tabs[1] }, browser.PinnedPair(tabs[0])?.Tabs);
        Assert.Same(browser.PinnedPair(tabs[0]), browser.PinnedPair(tabs[1]));
        Assert.NotNull(tabs[0].PinPair);
        Assert.Equal(tabs[0].PinPair, tabs[1].PinPair);
        Assert.Null(browser.PinnableSplit(tabs[0]));
        Assert.Equal(new[] { "one", "two", "three", "four" }, Titles(browser));
    }

    [Fact]
    public void UnpinLetsBothOutStillASplit()
    {
        var (browser, tabs) = Row();
        browser.Pin(tabs[2]);
        browser.Split(tabs[0], tabs[1]);
        browser.Pin(tabs[1]);
        Assert.Equal(new[] { "three", "one", "two", "four" }, Titles(browser));

        browser.Unpin(tabs[0]);
        Assert.False(tabs[0].Pinned || tabs[1].Pinned, "the other comes out too");
        Assert.Null(tabs[0].PinPair);
        Assert.Equal(new[] { tabs[0], tabs[1] }, browser.SplitOf(tabs[0])?.Tabs);
        Assert.Equal(new[] { "three", "one", "two", "four" }, Titles(browser));
        Assert.True(tabs[2].Pinned);
    }

    [Fact]
    public void APinnedSplitStaysRowsAndOutOfFolders()
    {
        var (browser, tabs) = Row();
        browser.Split(tabs[0], tabs[1]);
        browser.Pin(tabs[0]);
        browser.SetListed(tabs[0], false);
        Assert.True(tabs[0].Listed, "no Show as Square for a pinned split");
    }

    [Fact]
    public void UnpinnedAloneGoesToTheHeadOfTheLooseTabs()
    {
        var (browser, tabs) = Row();
        browser.Pin(tabs[0]);
        browser.Pin(tabs[1]);
        browser.Pin(tabs[2]);
        browser.Unpin(tabs[1]);
        Assert.Equal(new[] { "one", "three", "two", "four" }, Titles(browser));
    }

    [Fact]
    public void TwoPinsPutSideBySideBecomeAPinnedSplit()
    {
        var (browser, tabs) = Row();
        browser.Pin(tabs[0]);
        browser.Pin(tabs[1]);
        Assert.Null(browser.PinnedPair(tabs[0]));
        browser.Split(tabs[0], tabs[1]);
        Assert.NotNull(browser.PinnedPair(tabs[0]));
        Assert.True(tabs[0].Listed && tabs[1].Listed, "always rows");
        Assert.Equal(tabs[0].PinPair, tabs[1].PinPair);
        Assert.Equal(4, browser.Tabs.Count);
        Assert.Equal(new[] { "one", "two", "three", "four" }, Titles(browser));
    }

    [Fact]
    public void APageThatCantBeAPinIsPinnedAlone()
    {
        var (browser, tabs) = Row();
        var blank = browser.Open(null);
        browser.Split(tabs[0], blank);
        Assert.Null(browser.PinnableSplit(tabs[0]));
        browser.Pin(tabs[0]);
        Assert.True(tabs[0].Pinned && !blank.Pinned);
        Assert.Null(browser.SplitOf(tabs[0]));
        // A tab outside a split isn't offered.
        Assert.Null(browser.PinnableSplit(tabs[2]));
    }

    [Fact]
    public void PinsNamingOnePairStandAsOneSplit()
    {
        // As another window, or a restart, has them from pins.json.
        var (browser, tabs) = Row();
        browser.Pin(tabs[0]);
        browser.Pin(tabs[1]);
        browser.SetListed(tabs[0], true);
        browser.SetListed(tabs[1], true);
        var pair = Guid.NewGuid();
        tabs[0].PinPair = tabs[1].PinPair = pair;
        browser.KeepPinnedPairs();
        Assert.Equal(new[] { tabs[0], tabs[1] }, browser.PinnedPair(tabs[0])?.Tabs);
        browser.KeepPinnedPairs();
        Assert.Single(browser.Splits);

        // Let go of in pins.json: no longer one split here either.
        tabs[0].PinPair = tabs[1].PinPair = null;
        browser.KeepPinnedPairs();
        Assert.Empty(browser.Splits);
    }

    [Fact]
    public void PinsApartAreNotMadeASplit()
    {
        var (browser, tabs) = Row();
        browser.Pin(tabs[0]);
        browser.Pin(tabs[1]);
        browser.Pin(tabs[2]);
        tabs[0].PinPair = tabs[2].PinPair = Guid.NewGuid();
        browser.KeepPinnedPairs();
        Assert.Empty(browser.Splits);
    }
}
