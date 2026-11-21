using Search.Core;

namespace Search.Core.Tests;

public class TabGroupsTests
{
    private static TabGroup G(string name, bool collapsed = false) => new(Guid.NewGuid(), name, collapsed);
    private static GroupedTab T(Guid? group = null, bool pinned = false, bool @private = false, bool bench = false) => new(Guid.NewGuid(), group, pinned, @private, bench);
    private static Guid[] Ids(IEnumerable<GroupedTab> row) => row.Select(t => t.Id).ToArray();

    [Theory]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF", 0x6F9619FF)]
    [InlineData("FFFFFFFF-0000-0000-0000-000000000000", 0x7FFFFFFF)]
    [InlineData("80000000-1234-5678-9ABC-DEF012345678", 1)]
    [InlineData("00000000-0000-0000-0000-000000000000", 1)]
    [InlineData("00000102-AAAA-BBBB-CCCC-DDDDDDDDDDDD", 0x102)]
    public void TheNumberChromeWouldGiveIt(string id, int number) => Assert.Equal(number, TabGroups.Number(Guid.Parse(id)));

    [Fact]
    public void AGroupsOwnState()
    {
        var (a, b) = (G("A"), G("B", collapsed: true));
        IReadOnlyList<TabGroup> groups = [a, b];
        Assert.Equal("Group 3", TabGroups.NewName(groups));
        Assert.Equal("Work", TabGroups.Renamed(groups, a.Id, "  Work ")![0].Name);
        Assert.Null(TabGroups.Renamed(groups, a.Id, " "));
        Assert.Null(TabGroups.Renamed(groups, Guid.NewGuid(), "X"));
        Assert.True(TabGroups.Toggled(groups, a.Id)![0].Collapsed);
        Assert.Equal(3, TabGroups.Coloured(groups, a.Id, 3)![0].Colour);
        Assert.Null(TabGroups.Coloured(groups, a.Id, 0));
        Assert.Null(TabGroups.Coloured(groups, a.Id, TabGroups.ColourNames.Count));
        Assert.Null(TabGroups.Coloured([a with { Colour = 2 }], a.Id, 0)![0].Colour);
        Assert.All(TabGroups.FoldedAll(groups, true)!, g => Assert.True(g.Collapsed));
        Assert.Null(TabGroups.FoldedAll([b], true));
        Assert.True(TabGroups.CanFoldAll(groups) && TabGroups.CanUnfoldAll(groups));
        Assert.Equal(new[] { b, a }, TabGroups.Moved(groups, a.Id, 1)!);
        Assert.Null(TabGroups.Moved(groups, a.Id, 2));
        Assert.Null(TabGroups.Moved(groups, a.Id, 0));
    }

    [Fact]
    public void OnlyGroupsWithTabsComeBack()
    {
        var (kept, empty) = (G("Kept"), G("Empty"));
        var shape = new Session.Shape([new("u", "t") { GroupId = kept.Id }, new("p", "P") { Pin = "P", GroupId = empty.Id }], 0) { Groups = [kept, empty] };
        Assert.Equal(new[] { kept, empty }, TabGroups.Restored(shape));
        Assert.Equal(kept.Id, TabGroups.GroupFor(shape.Tabs[0], [kept]));
        Assert.Null(TabGroups.GroupFor(shape.Tabs[1], [kept, empty]));
        Assert.Null(TabGroups.GroupFor(new Session.Entry("u", "") { GroupId = Guid.NewGuid() }, [kept]));
        Assert.Empty(TabGroups.Restored(new Session.Shape([new("u", "t")], 0)));
        Assert.Equal(new[] { kept }, TabGroups.Kept([kept, empty], [null, kept.Id]));
    }

    [Fact]
    public void TheRowIsPinsThenGroupsThenTheRest()
    {
        var (a, b) = (G("A"), G("B"));
        var (pin, inB, loose1, inA, unknown, loose2) = (T(pinned: true), T(b.Id), T(), T(a.Id), T(Guid.NewGuid()), T());
        var row = TabGroups.Arranged([loose1, inB, pin, inA, unknown, loose2], [a, b]);
        Assert.Equal(Ids([pin, inA, inB, loose1, unknown, loose2]), Ids(row));
        Assert.Equal(new[] { unknown.Id, loose2.Id }, Ids(TabGroups.Members([loose1, unknown, loose2], [a, b], null)).Skip(1));
        Assert.Null(TabGroups.GroupOf(T(a.Id, pinned: true), [a]));
    }

    [Fact]
    public void ASplitsPagesStayTogether()
    {
        var a = G("A");
        var (left, right, loose, other) = (T(), T(), T(), T(a.Id));
        var split = TabSplit.Pair(left.Id, right.Id);
        var row = TabGroups.Arranged([left, loose, right, other], [a], [split]);
        Assert.Equal(Ids([other, left, right, loose]), Ids(row));
        var moved = TabGroups.Into([left, right, loose, other], [a], right.Id, a.Id, [split])!;
        Assert.Equal(Ids([left, right, other, loose]), Ids(moved.Row));
        Assert.All(moved.Row.Take(3), t => Assert.Equal(a.Id, t.Group));
    }

    [Fact]
    public void IntoAGroupAndOut()
    {
        var (a, b) = (G("A"), G("B", collapsed: true));
        var (only, loose, inB) = (T(a.Id), T(), T(b.Id));
        var moved = TabGroups.Into([only, loose, inB], [a, b], only.Id, b.Id)!;
        Assert.Equal(new[] { b.Id }, moved.Groups.Select(g => g.Id));
        Assert.False(moved.Groups[0].Collapsed);
        Assert.Equal(Ids([only, inB, loose]), Ids(moved.Row));
        Assert.Null(TabGroups.Into([only], [a], only.Id, a.Id));
        Assert.Null(TabGroups.Into([only], [a], only.Id, Guid.NewGuid()));
        Assert.Null(TabGroups.Into([T(pinned: true)], [a], Guid.NewGuid(), a.Id));
        var pin = T(pinned: true);
        Assert.Null(TabGroups.Into([pin], [a], pin.Id, a.Id));
        var shy = T(@private: true);
        Assert.Null(TabGroups.Into([shy], [a], shy.Id, a.Id));
        var outOf = TabGroups.Into([only, loose], [a], only.Id, null)!;
        Assert.Empty(outOf.Groups);
        Assert.Null(outOf.Row.First(t => t.Id == only.Id).Group);
    }

    [Fact]
    public void ANewGroupHoldsTheTab()
    {
        var a = G("A");
        var tab = T();
        var (result, made) = TabGroups.Add([tab], [a], tab.Id);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(("Group 2", made), (result.Groups[1].Name, (Guid?)result.Groups[1].Id));
        Assert.Equal(made, result.Row[0].Group);
        var pin = T(pinned: true);
        var (none, nothing) = TabGroups.Add([pin], [a], pin.Id);
        Assert.Null(nothing);
        Assert.Equal(new[] { a }, none.Groups);
    }

    [Fact]
    public void UngroupAndClose()
    {
        var a = G("A");
        var (one, two, loose) = (T(a.Id), T(a.Id), T());
        var ungrouped = TabGroups.Ungrouped([one, loose, two], [a], a.Id)!;
        Assert.Empty(ungrouped.Groups);
        Assert.All(ungrouped.Row, t => Assert.Null(t.Group));
        Assert.Null(TabGroups.Ungrouped([one], [a], Guid.NewGuid()));
        Assert.Equal(new[] { two.Id, one.Id }, TabGroups.Closing([one, loose, two], a.Id, new HashSet<Guid> { one.Id }));
        Assert.Equal(new[] { a }, TabGroups.Emptied([a], [one], a.Id));
        Assert.Empty(TabGroups.Emptied([a], [loose], a.Id));
        Assert.Equal(new[] { a }, TabGroups.Emptied([a], [loose], null));
    }

    [Fact]
    public void LetGoOnAGroup()
    {
        var (a, b) = (G("A", collapsed: true), G("B"));
        var (a1, a2, b1, loose) = (T(a.Id), T(a.Id), T(b.Id), T());
        IReadOnlyList<GroupedTab> row = [a1, a2, b1, loose];
        var before = TabGroups.Dropped(row, [a, b], [loose.Id], loose.Id, a.Id, member: a2.Id)!;
        Assert.Equal(Ids([a1, loose, a2, b1]), Ids(before.Row));
        Assert.False(before.Groups[0].Collapsed);
        var after = TabGroups.Dropped(row, [a, b], [loose.Id], loose.Id, a.Id, member: a1.Id, after: true)!;
        Assert.Equal(Ids([a1, loose, a2, b1]), Ids(after.Row));
        var end = TabGroups.Dropped(row, [a, b], [b1.Id], b1.Id, a.Id)!;
        Assert.Equal(new[] { a.Id }, end.Groups.Select(g => g.Id));
        Assert.Equal(Ids([a1, a2, b1, loose]), Ids(end.Row));
        Assert.Null(TabGroups.Dropped(row, [a, b], [loose.Id], loose.Id, a.Id, member: b1.Id));
        Assert.Null(TabGroups.Dropped(row, [a, b], [a1.Id], a1.Id, a.Id, member: a1.Id));
        var (p, q) = (T(a.Id), T(a.Id));
        var pair = TabSplit.Pair(p.Id, q.Id);
        var beside = TabGroups.Dropped([a1, p, q, loose], [a, b], [loose.Id], loose.Id, a.Id, member: p.Id, after: true, splits: [pair])!;
        Assert.Equal(Ids([a1, p, q, loose]), Ids(beside.Row));
        Assert.Equal(a.Id, beside.Row[3].Group);
    }

    [Fact]
    public void CarriedOutAmongTheLooseTabs()
    {
        var a = G("A");
        var (only, l1, l2) = (T(a.Id), T(), T());
        var left = TabGroups.Leaving([only, l1, l2], [a], [only.Id], l2.Id)!;
        Assert.Empty(left.Groups);
        Assert.Equal(Ids([l1, only, l2]), Ids(left.Row));
        Assert.Equal(Ids([l1, l2, only]), Ids(TabGroups.Leaving([only, l1, l2], [a], [only.Id])!.Row));
        Assert.Null(TabGroups.Leaving([l1, l2], [a], [l1.Id]));
    }

    [Fact]
    public void AFoldedGroupShowsTheTabInFront()
    {
        var (open, folded) = (G("Open"), G("Folded", collapsed: true));
        var (o1, f1, f2, pin) = (T(open.Id), T(folded.Id), T(folded.Id), T(pinned: true));
        IReadOnlyList<GroupedTab> row = [pin, o1, f1, f2];
        Assert.Empty(TabGroups.Visible(folded, [f1, f2], o1.Id));
        Assert.Equal(Ids([f2]), Ids(TabGroups.Visible(folded, [f1, f2], f2.Id)));
        Assert.Equal(Ids([pin, o1, f2]), Ids(TabGroups.Shown(row, [open, folded], f2.Id)));
    }
}
