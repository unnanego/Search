namespace Search.Core;

// Tab groups as a window keeps them (TabGroup.swift, the model parts of TabGroups.swift and Browser.swift): a named
// section of ordinary tabs in one space's row. Its tabs remain ordinary tabs and their own group ids say which group
// holds them; the group itself (TabGroup, in Session.cs) is a name, a fold and a colour, kept in the session file.
//
// The rules, as pure functions over GroupedTab, the facts a window's tab hands in: a pin, a private tab or a bench tab
// is in no group; a group lasts as long as its tabs (the last one gone, it goes too); a tab moved in unfolds its
// group; a split's pages move together; and the row is always shown pins first, then each group's tabs in the
// groups' order, then the tabs in none (Arranged, which the Mac runs after every change to the row while groups are
// on). What comes back is the row in its new order, each tab with its new group, for the window to apply.

/// What the group rules need of a window's tab, and what they hand back for it.
public readonly record struct GroupedTab(Guid Id, Guid? Group = null, bool Pinned = false, bool Private = false, bool Bench = false);

/// A row and its groups after a change: each tab with the group it is in now, in the order the row now has.
public sealed record Regrouped(IReadOnlyList<GroupedTab> Row, IReadOnlyList<TabGroup> Groups);

public static class TabGroups
{
    /// The colours a group (or a space) can wear, by index: 0 is none, and is written as no colour at all.
    public static readonly IReadOnlyList<string> ColourNames = ["None", "Blue", "Green", "Orange", "Red", "Violet"];

    /// The number an extension knows the group by, as Chrome numbers its groups. Taken from the identifier, so it
    /// stays the same for as long as the group lasts, across launches too, with nothing to keep.
    public static int Number(Guid id)
    {
        var u = id.ToByteArray(bigEndian: true);
        var n = (u[0] & 0x7F) << 24 | u[1] << 16 | u[2] << 8 | u[3];
        return n == 0 ? 1 : n;
    }

    /// What a new group is called, before you name it.
    public static string NewName(IReadOnlyList<TabGroup> groups) => $"Group {groups.Count + 1}";

    /// Whether a tab can be in a group: not a pin, a private tab, or a bench tab.
    public static bool CanJoin(GroupedTab tab) => !tab.Pinned && !tab.Private && !tab.Bench;

    /// The group a tab is shown in: none for a pin, or for a group this space doesn't have, which is shown with the
    /// loose tabs rather than lost.
    public static Guid? GroupOf(GroupedTab tab, IReadOnlyList<TabGroup> groups) => !tab.Pinned && tab.Group is { } id && groups.Any(g => g.Id == id) ? id : null;

    /// The tabs shown in `group` (null: those in none), in the row's order. Pins are in neither.
    public static IReadOnlyList<GroupedTab> Members(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, Guid? group) =>
        row.Where(t => !t.Pinned && GroupOf(t, groups) == group).ToList();

    /// The groups that still have a tab: a group with none is never written or brought back.
    public static IReadOnlyList<TabGroup> Kept(IReadOnlyList<TabGroup> groups, IEnumerable<Guid?> members)
    {
        var held = members.OfType<Guid>().ToHashSet();
        return groups.Where(g => held.Contains(g.Id)).ToList();
    }

    /// The groups a session brings back: those any of its tabs is in.
    public static IReadOnlyList<TabGroup> Restored(Session.Shape shape) => Kept(shape.Groups ?? [], shape.Tabs.Select(e => e.GroupId));

    /// The group a restored tab is in: none for a pin, or for a group the session doesn't have.
    public static Guid? GroupFor(Session.Entry entry, IReadOnlyList<TabGroup> groups) => entry.Pin is null && entry.GroupId is { } id && groups.Any(g => g.Id == id) ? id : null;

    /// A group lasts as long as its tabs: the last one gone, it goes too.
    public static IReadOnlyList<TabGroup> Emptied(IReadOnlyList<TabGroup> groups, IReadOnlyList<GroupedTab> row, Guid? id) =>
        id is { } gone && !row.Any(t => t.Group == gone) ? groups.Where(g => g.Id != gone).ToList() : groups;

    // A group's own state. Each gives back null when nothing changes.

    /// Renamed, trimmed; a name of nothing leaves it as it was.
    public static IReadOnlyList<TabGroup>? Renamed(IReadOnlyList<TabGroup> groups, Guid id, string name) =>
        name.Trim() is { Length: > 0 } trimmed && groups.Any(g => g.Id == id) ? groups.Select(g => g.Id == id ? g with { Name = trimmed } : g).ToList() : null;

    public static IReadOnlyList<TabGroup>? Toggled(IReadOnlyList<TabGroup> groups, Guid id) =>
        groups.Any(g => g.Id == id) ? groups.Select(g => g.Id == id ? g with { Collapsed = !g.Collapsed } : g).ToList() : null;

    /// Colour 0 is none, written as no colour; one past the palette is turned away.
    public static IReadOnlyList<TabGroup>? Coloured(IReadOnlyList<TabGroup> groups, Guid id, int colour)
    {
        if (colour < 0 || colour >= ColourNames.Count || groups.FirstOrDefault(g => g.Id == id) is not { } group) return null;
        int? choice = colour == 0 ? null : colour;
        return group.Colour == choice ? null : groups.Select(g => g.Id == id ? g with { Colour = choice } : g).ToList();
    }

    /// Fold All Groups / Unfold All Groups, as in Arc: every group of the space at once.
    public static IReadOnlyList<TabGroup>? FoldedAll(IReadOnlyList<TabGroup> groups, bool fold) =>
        groups.Any(g => g.Collapsed != fold) ? groups.Select(g => g with { Collapsed = fold }).ToList() : null;

    public static bool CanFoldAll(IReadOnlyList<TabGroup> groups) => groups.Any(g => !g.Collapsed);
    public static bool CanUnfoldAll(IReadOnlyList<TabGroup> groups) => groups.Any(g => g.Collapsed);

    /// A group carried to `index` among the groups (then the row Arranged again).
    public static IReadOnlyList<TabGroup>? Moved(IReadOnlyList<TabGroup> groups, Guid id, int index)
    {
        var from = groups.ToList().FindIndex(g => g.Id == id);
        if (from < 0 || index < 0 || index >= groups.Count || from == index) return null;
        var list = groups.ToList();
        var group = list[from];
        list.RemoveAt(from);
        list.Insert(index, group);
        return list;
    }

    // The row.

    /// The row in the order it is shown: the pins, each group's tabs in the groups' order, then the tabs in none, each
    /// split's pages together after its first. `splits` while Split View is on, none otherwise.
    public static IReadOnlyList<GroupedTab> Arranged(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, IReadOnlyList<TabSplit>? splits = null)
    {
        splits ??= [];
        var off = Splits.PairedOff(splits);
        var shown = row.Where(t => !off.Contains(t.Id)).ToList();
        var byId = new Dictionary<Guid, GroupedTab>();
        foreach (var tab in row) byId.TryAdd(tab.Id, tab);
        var pairs = new Dictionary<Guid, TabSplit>();
        foreach (var pair in splits) pairs.TryAdd(pair.Left, pair);
        IEnumerable<GroupedTab> order = [.. shown.Where(t => t.Pinned), .. groups.SelectMany(g => Members(shown, groups, g.Id)), .. Members(shown, groups, null)];
        return order.SelectMany(t => pairs.TryGetValue(t.Id, out var pair) ? pair.Tabs.Where(byId.ContainsKey).Select(id => byId[id]) : [t]).ToList();
    }

    /// A folded group shows its name alone, and the tab you are on if it is in there, so the page on screen always has
    /// its place in the row. `front`: the tab in front, or the first page of the split in front.
    public static IReadOnlyList<GroupedTab> Visible(TabGroup group, IReadOnlyList<GroupedTab> members, Guid? front, IReadOnlyList<TabSplit>? splits = null)
    {
        var off = Splits.PairedOff(splits ?? []);
        var shown = members.Where(t => !off.Contains(t.Id));
        return (group.Collapsed ? shown.Where(t => t.Id == front) : shown).ToList();
    }

    /// The tabs as they are on screen with groups on: the ones folded away in a group are not among them.
    public static IReadOnlyList<GroupedTab> Shown(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, Guid? front, IReadOnlyList<TabSplit>? splits = null)
    {
        var off = Splits.PairedOff(splits ?? []);
        return [.. row.Where(t => t.Pinned && !off.Contains(t.Id)), .. groups.SelectMany(g => Visible(g, Members(row, groups, g.Id), front, splits)), .. Members(row, groups, null).Where(t => !off.Contains(t.Id))];
    }

    /// A tab into a group, or out of every group (null): a pin, a private or bench tab can't be in one. A split's
    /// pages go with it and stay a split. The group it left goes if that was its last tab; the one it joined unfolds.
    /// Null when nothing changes.
    public static Regrouped? Into(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, Guid tab, Guid? group, IReadOnlyList<TabSplit>? splits = null)
    {
        if (Find(row, tab) is not { } moving || !CanJoin(moving) || (group is { } g && !groups.Any(x => x.Id == g)) || moving.Group == group) return null;
        var together = (Splits.Of(splits ?? [], tab)?.Tabs ?? [tab]).ToHashSet();
        var assigned = row.Select(t => together.Contains(t.Id) ? t with { Group = group } : t).ToList();
        var kept = Unfold(Emptied(groups, assigned, moving.Group), group);
        return new(Arranged(assigned, kept, splits), kept);
    }

    /// New Group from a tab's menu: a group named Group N holding the tab. A tab that can't be in one leaves none
    /// behind. `Made`: the new group's id, for its name to be typed (null when it went).
    public static (Regrouped Result, Guid? Made) Add(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, Guid tab, IReadOnlyList<TabSplit>? splits = null)
    {
        var made = new TabGroup(Guid.NewGuid(), NewName(groups));
        IReadOnlyList<TabGroup> more = [.. groups, made];
        var result = Into(row, more, tab, made.Id, splits) ?? new Regrouped(row, more);
        var kept = Emptied(result.Groups, result.Row, made.Id);
        return (result with { Groups = kept }, kept.Any(g => g.Id == made.Id) ? made.Id : null);
    }

    /// Ungroup Tabs: the group goes, its tabs stay, in none.
    public static Regrouped? Ungrouped(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, Guid id, IReadOnlyList<TabSplit>? splits = null)
    {
        if (!groups.Any(g => g.Id == id)) return null;
        var kept = groups.Where(g => g.Id != id).ToList();
        return new(Arranged(row.Select(t => t.Group == id ? t with { Group = null } : t).ToList(), kept, splits), kept);
    }

    /// Close Group: the tabs to close, each as Ctrl+W closes it, the ones on screen last so no neighbour wakes only to
    /// be closed. The group goes with its last tab (Emptied), and an empty one goes at once.
    public static IReadOnlyList<Guid> Closing(IReadOnlyList<GroupedTab> row, Guid id, IReadOnlySet<Guid> onScreen)
    {
        var going = row.Where(t => t.Group == id).ToList();
        return [.. going.Where(t => !onScreen.Contains(t.Id)).Select(t => t.Id), .. going.Where(t => onScreen.Contains(t.Id)).Select(t => t.Id)];
    }

    /// Tabs carried onto a group and let go there: in front of `member` (one of its tabs), after it with `after`, or
    /// at the end of the group; a member that is half of a split puts them beside the whole split. `moving` are the
    /// tabs carried (the tabs picked with it, a split whole); `detaching` carries `tab` alone, out of its split
    /// (Splits.Leave). The group unfolds, and a group they left empty goes. Null when nothing can move.
    public static Regrouped? Dropped(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, IReadOnlyList<Guid> moving, Guid tab, Guid group, Guid? member = null, bool after = false, bool detaching = false, IReadOnlyList<TabSplit>? splits = null)
    {
        if (!groups.Any(g => g.Id == group)) return null;
        var ids = (detaching ? [tab] : moving).ToHashSet();
        var carried = row.Where(t => ids.Contains(t.Id)).ToList();
        if (carried.Count == 0 || !carried.All(CanJoin)) return null;
        Guid? anchor = null;
        if (member is { } m)
        {
            if (Find(row, m) is not { } found || found.Group != group || !CanJoin(found) || ids.Contains(m)) return null;
            var pair = Splits.Of(splits ?? [], m) is { } p && !(detaching && p.Contains(tab)) ? p : null;
            anchor = pair is null ? m : after ? pair.Right : pair.Left;
        }
        var list = row.Where(t => !ids.Contains(t.Id)).ToList();
        var insertion = anchor is { } a && list.FindIndex(t => t.Id == a) is var at and >= 0 ? at + (after ? 1 : 0)
            : list.FindLastIndex(t => t.Group == group) is var last and >= 0 ? last + 1
            : list.FindIndex(t => !t.Pinned) is var loose and >= 0 ? loose : list.Count;
        var previous = carried.Select(t => t.Group).OfType<Guid>().Where(g => g != group).Distinct().ToList();
        list.InsertRange(insertion, carried.Select(t => t with { Group = group }));
        var kept = Unfold(groups, group);
        foreach (var gone in previous) kept = Emptied(kept, list, gone);
        return new(Arranged(list, kept, splits), kept);
    }

    /// Tabs carried out of their group and let go among the tabs in none: in front of `loose`, or after them all. A
    /// group left empty goes. Null unless every one may be in a group and one of them is in one.
    public static Regrouped? Leaving(IReadOnlyList<GroupedTab> row, IReadOnlyList<TabGroup> groups, IReadOnlyList<Guid> moving, Guid? loose = null, IReadOnlyList<TabSplit>? splits = null)
    {
        var ids = moving.ToHashSet();
        var carried = row.Where(t => ids.Contains(t.Id)).ToList();
        if (carried.Count == 0 || !carried.All(CanJoin) || !carried.Any(t => GroupOf(t, groups) is not null)) return null;
        var list = row.Where(t => !ids.Contains(t.Id)).ToList();
        var at = loose is { } before ? list.FindIndex(t => t.Id == before && !t.Pinned && GroupOf(t, groups) is null) : -1;
        list.InsertRange(at >= 0 ? at : list.Count, carried.Select(t => t with { Group = null }));
        IReadOnlyList<TabGroup> kept = groups;
        foreach (var gone in carried.Select(t => t.Group).OfType<Guid>().Distinct()) kept = Emptied(kept, list, gone);
        return new(Arranged(list, kept, splits), kept);
    }

    private static GroupedTab? Find(IReadOnlyList<GroupedTab> row, Guid id) { foreach (var t in row) if (t.Id == id) return t; return null; }

    private static IReadOnlyList<TabGroup> Unfold(IReadOnlyList<TabGroup> groups, Guid? id) => id is { } open ? groups.Select(g => g.Id == open ? g with { Collapsed = false } : g).ToList() : groups;
}
