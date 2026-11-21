using Search.Core;

namespace Search.Core.Tests;

public class ClosingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static IdleTab Left(double hoursAgo, double? letGoHoursAgo = null) =>
        new(Guid.NewGuid()) { Touched = Now.AddHours(-hoursAgo), LetGo = letGoHoursAgo is { } l ? Now.AddHours(-l) : null };

    [Fact]
    public void TheChoices()
    {
        Assert.Null(Closing.Interval(TabLife.Never));
        Assert.Equal(TimeSpan.FromHours(12), Closing.Interval(TabLife.HalfDay));
        Assert.Equal(TimeSpan.FromDays(1), Closing.Interval(TabLife.Day));
        Assert.Equal(TimeSpan.FromDays(7), Closing.Interval(TabLife.Week));
        Assert.Equal(TimeSpan.FromDays(30), Closing.Interval(TabLife.Month));
        Assert.Equal(TabLife.Week, Closing.Life(168));
        Assert.Equal(TabLife.Never, Closing.Life(5));
        Assert.Equal("After 12 hours", Closing.Title(TabLife.HalfDay));
        Assert.Equal(TimeSpan.FromSeconds(3), Closing.After(TabLife.Day, 3));
        Assert.Null(Closing.After(TabLife.Never, 3));
    }

    [Fact]
    public void TimeCountsFromTheLaterOfLookingAndLettingGo()
    {
        Assert.Equal(Now, Closing.LeftSince(Now, Now.AddHours(-1)));
        Assert.Equal(Now, Closing.LeftSince(Now.AddHours(-1), Now));
        Assert.Equal(Now, Closing.LeftSince(Now, null));
        var unpinnedLately = Left(48, letGoHoursAgo: 2);
        Assert.False(Closing.IsDue(unpinnedLately, Now, TimeSpan.FromHours(12)));
        Assert.True(Closing.IsDue(Left(13), Now, TimeSpan.FromHours(12)));
        Assert.True(Closing.IsDue(Left(12), Now, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void WhatYouKeepStays()
    {
        Assert.Null(Closing.Stays(Left(1)));
        Assert.Equal("on screen", Closing.Stays(Left(1) with { OnScreen = true, Pinned = true }));
        Assert.Equal("on screen in another window", Closing.Stays(Left(1) with { OnScreenElsewhere = true }));
        Assert.Equal("pinned", Closing.Stays(Left(1) with { Pinned = true }));
        Assert.Equal("named", Closing.Stays(Left(1) with { Named = true }));
        Assert.Equal("in a group", Closing.Stays(Left(1) with { Grouped = true }));
        Assert.Equal("private", Closing.Stays(Left(1) with { Private = true }));
        Assert.Equal("a bench tab", Closing.Stays(Left(1) with { Bench = true }));
        Assert.Equal("blank", Closing.Stays(Left(1) with { Blank = true }));
        Assert.Equal("kept awake for this site", Closing.Stays(Left(1) with { KeptAwake = true }));
        Assert.Equal("playing sound", Closing.Stays(Left(1) with { Noisy = true }));
        Assert.Equal("on a call", Closing.Stays(Left(1) with { OnCall = true }));
        Assert.Equal("downloading", Closing.Stays(Left(1) with { Downloading = true }));
        Assert.Equal("a question waiting", Closing.Stays(Left(1) with { Asking = true }));
        Assert.Null(Closing.Stays(Left(1) with { Asleep = true, HasPage = false, Loading = true, Parked = true }));
    }

    [Fact]
    public void DueTabsAndSplitsWholeOrNotAtAll()
    {
        var (old1, old2, fresh, pinned, pairOld1, pairOld2, mixedOld, mixedNew) = (Left(30), Left(25), Left(2), Left(100) with { Pinned = true }, Left(40), Left(50), Left(60), Left(1));
        IReadOnlyList<TabSplit> splits = [TabSplit.Pair(pairOld1.Id, pairOld2.Id), TabSplit.Pair(mixedOld.Id, mixedNew.Id)];
        var due = Closing.Due([old1, fresh, pairOld2, pinned, old2, pairOld1, mixedOld, mixedNew], splits, Now, TabLife.Day);
        Assert.Equal(3, due.Count);
        Assert.Equal(new[] { old1.Id }, due[0]);
        Assert.Equal(new[] { pairOld1.Id, pairOld2.Id }.Order(), due[1].Order());
        Assert.Equal(new[] { old2.Id }, due[2]);
        Assert.Empty(Closing.Due([old1], [], Now, TabLife.Never));
        Assert.Single(Closing.Due([fresh], [], Now, TabLife.Day, seconds: 60));
        Assert.Empty(Closing.Due([old1], [], Now, TabLife.Month));
    }

    [Fact]
    public void TheSessionCarriesTheTime()
    {
        var entry = new Session.Entry("https://a/", "A") { Touched = Now.AddDays(-3), LetGo = Now.AddDays(-1) };
        Assert.Equal(Now.AddDays(-3), Closing.Touched(entry, Now));
        Assert.Equal(Now.AddDays(-1), Closing.LetGo(entry, Now));
        var future = entry with { Touched = Now.AddYears(5), LetGo = Now.AddYears(5) };
        Assert.Equal(Now, Closing.Touched(future, Now));
        Assert.Equal(Now, Closing.LetGo(future, Now));
        var old = new Session.Entry("https://a/", "A");
        Assert.Equal(Now, Closing.Touched(old, Now));
        Assert.Null(Closing.LetGo(old, Now));
        Assert.Equal(Now, Closing.ToWrite(Now.AddHours(-5), onScreen: true, Now));
        Assert.Equal(Now.AddHours(-5), Closing.ToWrite(Now.AddHours(-5), onScreen: false, Now));
    }
}
