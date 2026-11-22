using Search.Core;
using Xunit;

namespace Search.Core.Tests;

public sealed class TabSearchTests
{
    [Theory]
    [InlineData("GitHub", "github.com/search", "git", 3)]
    [InlineData("Pull requests", "github.com/pulls", "github.com/p", 3)]
    [InlineData("Pull requests · GitHub", "github.com/pulls", "pull git", 2)]
    [InlineData("Inbox", "mail.example.com", "exa", 2)]
    [InlineData("Inbox", "mail.example.com", "xam", 1)]
    [InlineData("Inbox", "mail.example.com", "e", 2)]
    [InlineData("Inbox", "mail.example.com", "x", 0)]
    [InlineData("Inbox", "mail.example.com", "nope", 0)]
    [InlineData("Inbox", "mail.example.com", "  ", 0)]
    public void RanksAsTheMacDoes(string label, string address, string typed, int score) => Assert.Equal(score, TabSearch.Relevance(label, address, typed));

    [Fact]
    public void OpenTabsFirstThenAtMostTwoPins()
    {
        var now = DateTimeOffset.UtcNow;
        var tabs = new (string Label, bool Pin, int Ago)[] { ("git a", false, 5), ("git b", false, 1), ("git c", true, 2), ("git d", false, 3), ("git e", false, 4), ("git f", true, 1), ("git g", true, 0), ("other", false, 0) };
        var ranked = TabSearch.Ranked(tabs, "git", t => t.Label, _ => "", t => t.Pin, t => now.AddMinutes(-t.Ago));
        Assert.Equal(["git b", "git d", "git e", "git g", "git f"], ranked.Select(t => t.Label));
    }

    [Theory]
    [InlineData("git", "github.com/pulls", "hub.com/pulls")]
    [InlineData("GIT", "github.com", "hub.com")]
    [InlineData("git ", "github.com", null)]
    [InlineData("pull", "github.com/pulls", null)]
    [InlineData("github.com", "github.com", null)]
    public void FinishesOnlyWhatStartsTheAddress(string typed, string address, string? ending) => Assert.Equal(ending, TabSearch.Ending(typed, address));
}
