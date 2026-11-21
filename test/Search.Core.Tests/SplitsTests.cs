using Search.Core;

namespace Search.Core.Tests;

public class SplitsTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid(), E = Guid.NewGuid();

    private static void Near(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i], 9);
        Assert.Equal(1, actual.Sum(), 9);
    }

    [Fact]
    public void MadeEvenUnlessToldOtherwise()
    {
        var three = new TabSplit([A, B, C], focused: D);
        Near([1 / 3.0, 1 / 3.0, 1 / 3.0], three.Sizes);
        Assert.Null(three.Focused);
        Assert.Equal((A, C), (three.Left, three.Right));
        Near([0.5, 0.5], new TabSplit([A, B], sizes: [0.3]).Sizes);
        Near([0.8, 0.2], TabSplit.Pair(A, B, 0.95).Sizes);
        Assert.Equal(0.8, TabSplit.Pair(A, B, 0.95).Fraction, 9);
        Near([0.5, 0.5], TabSplit.Pair(A, B, double.NaN).Sizes);
        Near([0.3, 0.7], TabSplit.Pair(A, B).WithFraction(0.3).Sizes);
    }

    [Fact]
    public void ClampRaisesThePagesUnderTheirShare()
    {
        Near([0.1, 0.45, 0.45], TabSplit.Clamp([0.02, 0.49, 0.49]));
        Near([1 / 3.0, 1 / 3.0, 1 / 3.0], TabSplit.Clamp([0, 1, 1]));
        Near([0.25, 0.25, 0.25, 0.25], TabSplit.Clamp([1, 1, double.PositiveInfinity, -3]));
        Near([0.5, 0.5], TabSplit.Clamp([double.NaN, 4]));
        Assert.Equal(0.2, TabSplit.Least(2));
        Assert.Equal(0.1, TabSplit.Least(4));
        Near([0.25, 0.25, 0.25, 0.25], TabSplit.Even(4));
    }

    [Fact]
    public void PagesComeAndGo()
    {
        var pair = TabSplit.Pair(A, B, 0.7);
        var three = pair.Insert(C, A)!;
        Assert.Equal(new[] { A, C, B }, three.Tabs);
        Near([1 / 3.0, 1 / 3.0, 1 / 3.0], three.Sizes);
        Assert.Equal(new[] { C, A, B }, pair.Insert(C, A, before: true)!.Tabs);
        Assert.Equal(new[] { A, B, C }, pair.Insert(C)!.Tabs);
        Assert.Null(pair.Insert(A));
        var four = three.Insert(D)!;
        Assert.True(four.IsFull);
        Assert.Null(four.Insert(E));
        var (rest, stands) = (four with { Sizes = [0.4, 0.2, 0.2, 0.2], Focused = A }).Remove(A);
        Assert.True(stands);
        Assert.Equal(new[] { C, B, D }, rest.Tabs);
        Near([1 / 3.0, 1 / 3.0, 1 / 3.0], rest.Sizes);
        Assert.Null(rest.Focused);
        var (one, alone) = pair.Remove(B);
        Assert.False(alone);
        Assert.Equal(new[] { A }, one.Tabs);
        Assert.True(pair.Remove(E).Stands);
    }

    [Fact]
    public void NeighboursAndOrder()
    {
        var three = new TabSplit([A, B, C], focused: B);
        Assert.Equal(B, three.Partner(A));
        Assert.Equal(B, three.Partner(C));
        Assert.Null(three.Partner(D));
        Assert.True(three.IsBefore(A, C));
        Assert.False(three.IsBefore(C, A));
        Assert.False(three.IsBefore(A, null));
        Assert.Equal(new[] { A, C }, three.Others(B));
        var swapped = (three with { Sizes = [0.5, 0.3, 0.2] }).Swapped();
        Assert.Equal(new[] { C, B, A }, swapped.Tabs);
        Assert.Equal(new[] { 0.2, 0.3, 0.5 }, swapped.Sizes);
        var replaced = three.Replace(B, D);
        Assert.Equal(new[] { A, D, C }, replaced.Tabs);
        Assert.Equal(D, replaced.Focused);
        Assert.Null(three.WithSizes([0.5, 0.5]));
        Assert.Null(three.WithSizes([0.5, double.NaN, 0.5]));
        Assert.Null(three.Evened());
        Assert.NotNull(three.WithSizes([2, 1, 1]));
    }

    [Fact]
    public void WhatHolds()
    {
        var pair = TabSplit.Pair(A, B);
        Assert.True(Splits.Holds(pair, [new(C), new(A), new(B)]));
        Assert.False(Splits.Holds(pair, [new(A), new(C), new(B)]));
        Assert.False(Splits.Holds(pair, [new(B), new(A)]));
        Assert.False(Splits.Holds(pair, [new(A)]));
        Assert.False(Splits.Holds(pair, [new(A, Pinned: true), new(B)]));
        Assert.False(Splits.Holds(pair, [new(A, Private: true), new(B)]));
        Assert.False(Splits.Holds(pair, [new(A, Group: Guid.NewGuid()), new(B)]));
        Assert.True(Splits.CanSplit(new(A, Pinned: true), new(B), [new(A, Pinned: true), new(B)]));
        Assert.False(Splits.CanSplit(new(A), new(B, Bench: true), [new(A), new(B, Bench: true)]));
        Assert.False(Splits.CanSplit(new(A), new(B, Private: true), [new(A), new(B, Private: true)]));
        Assert.False(Splits.CanSplit(new(A), new(A), [new(A)]));
        Assert.False(Splits.CanSplit(new(A), new(B), [new(A)]));
        var full = new TabSplit([A, B, C, D]);
        IReadOnlyList<SplitTab> row = [new(A), new(B), new(C), new(D), new(E)];
        Assert.False(Splits.CanJoin(new(E), new(A), row, [full]));
        Assert.True(Splits.CanJoin(new(B), new(A), row, [full]));
        Assert.True(Splits.CanJoin(new(E), new(A), row, [pair]));
    }

    [Fact]
    public void APageLeavingOrClosing()
    {
        var three = new TabSplit([A, B, C], focused: B);
        var pair = TabSplit.Pair(D, E);
        var left = Splits.Leave([three, pair], B, front: C);
        Assert.Equal(new[] { A, C }, left[0].Tabs);
        Assert.Equal(C, left[0].Focused);
        Assert.Single(Splits.Leave([three, pair], D));
        Assert.Equal(2, Splits.Leave([three, pair], Guid.NewGuid()).Count);
        var (after, next) = Splits.Closed([three, pair], C);
        Assert.Equal(B, next);
        Assert.Equal(new[] { A, B }, after[0].Tabs);
        Assert.Null(Splits.Closed([pair], A).Next);
        Assert.Equal(new[] { pair }, Splits.Detach([three, pair], A));
        Assert.Equal(new[] { pair }, Splits.Pruned([three, pair], new HashSet<Guid> { A, B, D, E }));
        Assert.Equal(new[] { A, E, C }, Splits.Replaced([three], B, E)[0].Tabs);
    }

    [Fact]
    public void WhatIsShown()
    {
        var three = new TabSplit([A, B, C]);
        Assert.Equal(new HashSet<Guid> { B, C }, Splits.PairedOff([three]));
        Assert.True(Splits.StandsInRow([three], A));
        Assert.False(Splits.StandsInRow([three], B));
        Assert.True(Splits.StandsInRow([three], D));
        Assert.Equal(new HashSet<Guid> { A, B, C }, Splits.Visible([three], B));
        Assert.Equal(new HashSet<Guid> { D }, Splits.Visible([three], D));
        Assert.Empty(Splits.Visible([three], null));
    }

    [Fact]
    public void ToTheSessionAndBack()
    {
        var three = new TabSplit([A, B, C], SplitAxis.Vertical, [0.5, 0.3, 0.2], focused: C);
        var written = Splits.ToSession([three, TabSplit.Pair(D, E)], new Dictionary<Guid, int> { [A] = 3, [B] = 4, [C] = 5, [D] = 0 });
        var saved = Assert.Single(written);
        Assert.Equal(new[] { 3, 4, 5 }, saved.Tabs);
        Assert.Equal((SplitAxis.Vertical, (int?)5), (saved.Axis, saved.Focused));
        Assert.Equal(new[] { 0.5, 0.3, 0.2 }, saved.Sizes);
        var shortOne = Assert.Single(Splits.ToSession([three], new Dictionary<Guid, int> { [A] = 0, [B] = 1 }));
        Near([0.5, 0.5], shortOne.Sizes);
        Assert.Null(shortOne.Focused);
        Assert.Empty(Splits.ToSession([three], new Dictionary<Guid, int> { [A] = 0, [B] = 2, [C] = 3 }));

        var bySaved = new Dictionary<int, Guid> { [0] = A, [1] = B, [2] = C, [3] = D };
        IReadOnlyList<SplitTab> row = [new(A), new(B), new(C), new(D)];
        var back = Splits.FromSession([new Session.Split([0, 1], [0.6, 0.4]) { Axis = SplitAxis.Vertical, Focused = 1 }, new Session.Split([1, 2], [0.5, 0.5]), new Session.Split([2, 9], [0.5, 0.5]), new Session.Split([2, 3], [0.5, 0.5])], bySaved, row);
        Assert.Equal(2, back.Count);
        Assert.Equal(new[] { A, B }, back[0].Tabs);
        Assert.Equal((SplitAxis.Vertical, (Guid?)B), (back[0].Axis, back[0].Focused));
        Near([0.6, 0.4], back[0].Sizes);
        Assert.Equal(new[] { C, D }, back[1].Tabs);
        Assert.Empty(Splits.FromSession([new Session.Split([0, 1, 2, 3, 0], [0.2, 0.2, 0.2, 0.2, 0.2])], bySaved, row));
        Assert.Empty(Splits.FromSession([new Session.Split([0, 1], [0.5, 0.5])], bySaved, [new(A, Pinned: true), new(B)]));
    }
}
