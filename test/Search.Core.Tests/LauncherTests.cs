using Search.Core;
using Xunit;

namespace Search.Core.Tests;

public sealed class LauncherTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", LauncherKey.Control | LauncherKey.Alt, 0x20)]
    [InlineData("ctrl+shift+k", LauncherKey.Control | LauncherKey.Shift, 0x4B)]
    [InlineData("Alt+F12", LauncherKey.Alt, 0x7B)]
    [InlineData("Win+Shift+F9", LauncherKey.Win | LauncherKey.Shift, 0x78)]
    [InlineData("Ctrl+Alt+;", LauncherKey.Control | LauncherKey.Alt, 0xBA)]
    [InlineData("Ctrl+Alt+7", LauncherKey.Control | LauncherKey.Alt, 0x37)]
    public void ParsesWhatSettingsWrites(string text, int modifiers, int code)
    {
        var key = LauncherKey.Parse(text);
        Assert.Equal(new LauncherKey(modifiers, code), key);
        Assert.Equal(key, LauncherKey.Parse(key!.Display));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Nothing")]
    public void RefusesWhatIsNoKey(string? text) => Assert.Null(LauncherKey.Parse(text));

    [Fact]
    public void ShowsModifiersInWindowsOrder() => Assert.Equal("Ctrl+Alt+Shift+Win+Space", new LauncherKey(15, 0x20).Display);

    [Fact]
    public void SuggestsCtrlAltSpace() { Assert.Equal("Ctrl+Alt+Space", LauncherKey.Suggested.Display); Assert.Null(LauncherKey.Suggested.Refused()); }

    [Theory]
    [InlineData("Shift+A")]
    [InlineData("Space")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Shift+Tab")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Win+L")]
    [InlineData("Win+Shift+S")]
    [InlineData("Win+Space")]
    [InlineData("Win+Left")]
    public void LeavesWindowsItsOwnKeys(string text) => Assert.NotNull(LauncherKey.Parse(text)!.Refused());

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Alt+Shift+F")]
    [InlineData("Win+F9")]
    [InlineData("Ctrl+Alt+Shift+F9")]
    public void TakesAnyOtherKey(string text) => Assert.Null(LauncherKey.Parse(text)!.Refused());

    [Theory]
    [InlineData(true, true, false, false, false, true)]     // another app in front
    [InlineData(true, true, false, false, true, true)]      // Search in front, every window minimised
    [InlineData(true, true, false, true, true, false)]      // Search in front with a window: the page's key
    [InlineData(true, true, true, true, true, true)]        // up: pressed again it goes away
    [InlineData(false, true, false, false, false, false)]   // switched off
    [InlineData(true, false, false, false, false, false)]   // no key
    public void WantsTheKeyOnlyAwayFromItsWindows(bool on, bool hasKey, bool up, bool windowShowing, bool searchInFront, bool wants) =>
        Assert.Equal(wants, LauncherRules.Wants(on, hasKey ? LauncherKey.Suggested : null, recording: false, up, searchInFront, windowShowing));

    [Fact]
    public void GivesTheKeyUpWhileSettingsRecordsOne() => Assert.False(LauncherRules.Wants(true, LauncherKey.Suggested, recording: true, up: false, searchInFront: false, windowShowing: false));

    [Fact]
    public void PutsOpenPagesFirstAndNeverTwice()
    {
        var id = Guid.NewGuid();
        Suggestion Place(string url, SuggestionKind kind) => new(url, url, new Uri(url), kind);
        var open = new[]
        {
            Place("https://a.example/", SuggestionKind.Open) with { Tab = id },
            Place("https://b.example/", SuggestionKind.Open) with { Tab = Guid.NewGuid() },
            Place("https://c.example/", SuggestionKind.Open) with { Tab = Guid.NewGuid() },
            Place("https://d.example/", SuggestionKind.Open) with { Tab = Guid.NewGuid() }
        };
        var places = new[] { Place("https://a.example/", SuggestionKind.Visited), Place("https://e.example/", SuggestionKind.Bookmark), Place("https://google.com/search?q=a", SuggestionKind.Search) };
        var list = LauncherRules.Arrange(open, places);
        Assert.Equal(["https://a.example/", "https://b.example/", "https://c.example/", "https://e.example/", "https://google.com/search?q=a"], list.Select(s => s.Url.AbsoluteUri));
    }

    [Fact]
    public void WalksOffEitherEndToWhatWasTyped()
    {
        Assert.Equal(0, LauncherRules.Walk(null, 1, 3));
        Assert.Equal(2, LauncherRules.Walk(null, -1, 3));
        Assert.Equal(1, LauncherRules.Walk(0, 1, 3));
        Assert.Null(LauncherRules.Walk(2, 1, 3));
        Assert.Null(LauncherRules.Walk(0, -1, 3));
        Assert.Null(LauncherRules.Walk(null, 1, 0));
    }
}
