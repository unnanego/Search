namespace Search.Core;

// Tabs shown side by side, sharing one place in the row (Split.swift; the split rules of Browser.swift). The first
// page is the split's place among the others; the rest follow it, next to it. Two to four pages (Most), side by
// side or one above the other, each with its share of the stage, adding up to 1. The session file keeps them as
// Session.Split, its pages by their place in the file, and one written with two reads as it always did.
//
// A split is a value: every change gives back a new one. The rules a window's row follows with them (whether a split
// still holds, a page leaving or closing, what is shown) are pure functions in Splits over SplitTab facts.

public sealed record TabSplit
{
    /// The most pages one split holds, as Arc's: past four side by side, each is too narrow to read in most windows.
    public const int Most = 4;

    public Guid Id { get; init; }
    /// In the order they are shown.
    public IReadOnlyList<Guid> Tabs { get; init; }
    public SplitAxis Axis { get; init; }
    /// Each page's share of the stage, in the same order, adding up to 1.
    public IReadOnlyList<double> Sizes { get; init; }
    /// The page last focused, to come back to.
    public Guid? Focused { get; init; }

    /// Sizes that don't match the pages are made even; a focused page not in it is none.
    public TabSplit(IReadOnlyList<Guid> tabs, SplitAxis axis = SplitAxis.Horizontal, IReadOnlyList<double>? sizes = null, Guid? focused = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Tabs = tabs.ToList();
        Axis = axis;
        Sizes = sizes is not null && sizes.Count == tabs.Count ? Clamp(sizes) : Even(tabs.Count);
        Focused = focused is { } f && tabs.Contains(f) ? f : null;
    }

    public static TabSplit Pair(Guid left, Guid right, double fraction = 0.5) => new([left, right], sizes: [fraction, 1 - fraction]);

    /// The first page: the split's place in the row.
    public Guid Left => Tabs[0];
    /// The last page.
    public Guid Right => Tabs[^1];
    /// The first page's share.
    public double Fraction => Sizes.Count > 0 ? Sizes[0] : 0.5;
    /// No room for another page.
    public bool IsFull => Tabs.Count >= Most;

    public bool Contains(Guid tab) => Tabs.Contains(tab);

    /// The page beside this one: the next to its right, or, for the last, the one to its left. Of two, the other one.
    /// Where the keys go when this one leaves.
    public Guid? Partner(Guid tab)
    {
        var index = Tabs.ToList().IndexOf(tab);
        if (index < 0 || Tabs.Count < 2) return null;
        return index + 1 < Tabs.Count ? Tabs[index + 1] : Tabs[index - 1];
    }

    /// Whether `tab` stands before `other` in it (to the left, side by side).
    public bool IsBefore(Guid tab, Guid? other) => other is { } o && Tabs.ToList() is var list && list.IndexOf(tab) is var a and >= 0 && list.IndexOf(o) is var b and >= 0 && a < b;

    /// Every page but this one, in order.
    public IReadOnlyList<Guid> Others(Guid tab) => Tabs.Where(t => t != tab).ToList();

    public TabSplit WithFraction(double fraction) => this with { Sizes = Clamp([fraction, 1 - fraction]) };

    public TabSplit Replace(Guid old, Guid replacement) => this with { Tabs = Tabs.Select(t => t == old ? replacement : t).ToList(), Focused = Focused == old ? replacement : Focused };

    /// A page added beside `neighbour` (after it, or before it), or at the end, and the pages made even, as each one
    /// coming in is given an equal share. Null when full, or already in it.
    public TabSplit? Insert(Guid tab, Guid? neighbour = null, bool before = false)
    {
        if (IsFull || Contains(tab)) return null;
        var list = Tabs.ToList();
        var at = neighbour is { } n && list.IndexOf(n) is var i and >= 0 ? (before ? i : i + 1) : list.Count;
        list.Insert(at, tab);
        return this with { Tabs = list, Sizes = Even(list.Count) };
    }

    /// A page out of it: the others keep their order, and its share goes to them in proportion. `Stands`: whether two
    /// or more are left; one alone is no split, and the caller lets it go.
    public (TabSplit Split, bool Stands) Remove(Guid tab)
    {
        var index = Tabs.ToList().IndexOf(tab);
        if (index < 0) return (this, Tabs.Count >= 2);
        var tabs = Tabs.Where((_, i) => i != index).ToList();
        var sizes = Sizes.Where((_, i) => i != index).ToList();
        if (sizes.Count != tabs.Count) sizes = Even(tabs.Count).ToList();
        var total = sizes.Sum();
        IReadOnlyList<double> shares = tabs.Count > 0 && double.IsFinite(total) && total > 0 ? Clamp(sizes.Select(s => s / total).ToList()) : sizes;
        return (this with { Tabs = tabs, Sizes = shares, Focused = Focused == tab ? null : Focused }, tabs.Count >= 2);
    }

    /// The divider's menu › Swap Pages: of three or four, the order turned round.
    public TabSplit Swapped() => this with { Tabs = Tabs.Reverse().ToList(), Sizes = Sizes.Reverse().ToList() };

    /// Each page's share at once (two as a fraction), clamped. Null for shares that don't fit its pages or aren't
    /// numbers, or that change nothing.
    public TabSplit? WithSizes(IReadOnlyList<double> sizes)
    {
        if (sizes.Count != Tabs.Count || !sizes.All(double.IsFinite)) return null;
        var clamped = Clamp(sizes);
        return clamped.SequenceEqual(Sizes) ? null : this with { Sizes = clamped };
    }

    /// The divider's menu › Even Out, or a double-click on it.
    public TabSplit? Evened() => WithSizes(Even(Tabs.Count));

    /// The narrowest share a page has, of `count`: a fifth of the stage for two, as it always was; a tenth for three or four.
    public static double Least(int count) => count <= 2 ? 0.2 : 0.1;

    /// No page narrower than its least share; shares that add up to 1.
    public static IReadOnlyList<double> Clamp(IReadOnlyList<double> sizes)
    {
        if (sizes.Count == 2)
        {
            var first = Math.Min(0.8, Math.Max(0.2, double.IsFinite(sizes[0]) ? sizes[0] : 0.5));
            return [first, 1 - first];
        }
        var finite = sizes.Select(s => double.IsFinite(s) && s > 0 ? s : 1).ToList();
        var total = finite.Sum();
        if (total <= 0) return Even(sizes.Count);
        var shares = finite.Select(s => s / total).ToList();
        if (shares.Count <= 2) return shares;
        // Raise any page under its least share, taking the difference from the pages above it in proportion to what
        // they have over theirs.
        var least = Least(shares.Count);
        var shortBy = shares.Sum(s => Math.Max(0, least - s));
        if (shortBy <= 0) return shares;
        var spare = shares.Sum(s => Math.Max(0, s - least));
        return shares.Select(s => s < least ? least : s - (s - least) / spare * shortBy).ToList();
    }

    public static IReadOnlyList<double> Even(int count) => Enumerable.Repeat(1 / (double)Math.Max(1, count), count).ToList();

    public bool Equals(TabSplit? other) => other is not null && Id == other.Id && Tabs.SequenceEqual(other.Tabs) && Axis == other.Axis && Sizes.SequenceEqual(other.Sizes) && Focused == other.Focused;
    public override int GetHashCode() => HashCode.Combine(Id, Axis, Focused, Tabs.Count);
}

/// What the split rules need of a window's tab.
public readonly record struct SplitTab(Guid Id, bool Pinned = false, bool Bench = false, bool Private = false, Guid? Group = null);

public static class Splits
{
    /// The split a tab is in (pass none while Split View is off: kept, as groups are, and nothing acts on them).
    public static TabSplit? Of(IReadOnlyList<TabSplit> splits, Guid tab) => splits.FirstOrDefault(s => s.Contains(tab));

    /// The splits' later pages, which stand in no place of their own in the row.
    public static IReadOnlySet<Guid> PairedOff(IReadOnlyList<TabSplit> splits) => splits.SelectMany(s => s.Tabs.Where(t => t != s.Left)).ToHashSet();

    /// Whether a tab has a place of its own in the row: any tab but a split's later pages.
    public static bool StandsInRow(IReadOnlyList<TabSplit> splits, Guid tab) => Of(splits, tab) is not { } s || s.Left == tab;

    /// What is on screen: the tab in front, or every page of the split it is in.
    public static IReadOnlySet<Guid> Visible(IReadOnlyList<TabSplit> splits, Guid? front) =>
        front is not { } id ? new HashSet<Guid>() : Of(splits, id) is { } s ? s.Tabs.ToHashSet() : new HashSet<Guid> { id };

    /// Whether a split can stand as it is in a row: all its pages there, next to each other in its order, none pinned
    /// or a bench tab, of one kind (private or not) and one group. One kept while Split View was off may have come apart.
    public static bool Holds(TabSplit split, IReadOnlyList<SplitTab> row)
    {
        var places = split.Tabs.Select(id => row.ToList().FindIndex(t => t.Id == id)).Where(i => i >= 0).ToList();
        if (split.Tabs.Count < 2 || places.Count != split.Tabs.Count || !places.SequenceEqual(Enumerable.Range(places[0], places.Count))) return false;
        var first = row[places[0]];
        return places.Select(i => row[i]).All(t => !t.Pinned && !t.Bench && t.Private == first.Private && t.Group == first.Group);
    }

    /// Whether two tabs can be split together: two tabs of the row, neither a bench tab, of one kind. A pin can: its
    /// page goes into the split as a tab of its own, the pin staying where it is.
    public static bool CanSplit(SplitTab first, SplitTab second, IReadOnlyList<SplitTab> row) =>
        first.Id != second.Id && !first.Bench && !second.Bench && first.Private == second.Private && row.Any(t => t.Id == first.Id) && row.Any(t => t.Id == second.Id);

    /// Whether `tab` can go beside `target`: into the split `target` is in while it has room (moving within its own is
    /// always room enough), or as a pair with it.
    public static bool CanJoin(SplitTab tab, SplitTab target, IReadOnlyList<SplitTab> row, IReadOnlyList<TabSplit> splits) =>
        CanSplit(tab, target, row) && (Of(splits, target.Id) is not { } there || there.Contains(tab.Id) || !there.IsFull);

    /// One page out of its split, the others staying together, the keys with the page in front if they were on the one
    /// that left; a split left with one page is no split, and that page is a tab again.
    public static IReadOnlyList<TabSplit> Leave(IReadOnlyList<TabSplit> splits, Guid tab, Guid? front = null)
    {
        var index = splits.ToList().FindIndex(s => s.Contains(tab));
        if (index < 0) return splits;
        var list = splits.ToList();
        var (rest, stands) = list[index].Remove(tab);
        if (stands) list[index] = rest.Focused is null && front is { } f && rest.Contains(f) ? rest with { Focused = f } : rest;
        else list.RemoveAt(index);
        return list;
    }

    /// The whole split taken apart: every page stays, as a tab of its own.
    public static IReadOnlyList<TabSplit> Detach(IReadOnlyList<TabSplit> splits, Guid tab) => splits.Where(s => !s.Contains(tab)).ToList();

    /// A page of a split closed: it leaves its split, and the keys go to its partner (null when it was in none).
    public static (IReadOnlyList<TabSplit> Splits, Guid? Next) Closed(IReadOnlyList<TabSplit> splits, Guid tab, Guid? front = null) =>
        (Leave(splits, tab, front), Of(splits, tab)?.Partner(tab));

    /// The splits whose pages are all still in the row: a pin taken away in another window takes its split with it.
    public static IReadOnlyList<TabSplit> Pruned(IReadOnlyList<TabSplit> splits, IReadOnlySet<Guid> row) => splits.Where(s => s.Tabs.All(row.Contains)).ToList();

    /// One page in another's place (an empty page given an open tab).
    public static IReadOnlyList<TabSplit> Replaced(IReadOnlyList<TabSplit> splits, Guid old, Guid replacement) => splits.Select(s => s.Contains(old) ? s.Replace(old, replacement) : s).ToList();

    /// The splits as the session file keeps them, by each page's place in the file (`places`: a tab's index there; an
    /// empty or private page isn't written). Two or more pages written, next to each other, stay a split, their
    /// shares made up between them; one alone comes back as a tab.
    public static IReadOnlyList<Session.Split> ToSession(IReadOnlyList<TabSplit> splits, IReadOnlyDictionary<Guid, int> places)
    {
        var made = new List<Session.Split>();
        foreach (var split in splits)
        {
            var written = Enumerable.Range(0, split.Tabs.Count).Where(i => places.ContainsKey(split.Tabs[i])).ToList();
            var at = written.Select(i => places[split.Tabs[i]]).ToList();
            if (at.Count < 2 || !at.SequenceEqual(Enumerable.Range(at[0], at.Count))) continue;
            var sizes = written.Count == split.Tabs.Count || split.Sizes.Count != split.Tabs.Count ? split.Sizes : TabSplit.Clamp(written.Select(i => split.Sizes[i]).ToList());
            made.Add(new Session.Split(at, sizes) { Axis = split.Axis, Focused = split.Focused is { } f && places.TryGetValue(f, out var p) ? p : null });
        }
        return made;
    }

    /// The splits a session brings back (`bySaved`: the tab made for each entry, by its place in the file): every page
    /// there, four at most, each tab in one split at most, and only a split that still holds in `row`.
    public static IReadOnlyList<TabSplit> FromSession(IReadOnlyList<Session.Split> saved, IReadOnlyDictionary<int, Guid> bySaved, IReadOnlyList<SplitTab> row)
    {
        var made = new List<TabSplit>();
        var taken = new HashSet<Guid>();
        foreach (var split in saved)
        {
            var members = split.Tabs.Where(bySaved.ContainsKey).Select(i => bySaved[i]).ToList();
            if (members.Count != split.Tabs.Count || members.Count > TabSplit.Most || members.Any(taken.Contains)) continue;
            var pair = new TabSplit(members, split.Axis, split.Sizes, split.Focused is { } f && bySaved.TryGetValue(f, out var id) ? id : null);
            if (!Holds(pair, row)) continue;
            made.Add(pair);
            taken.UnionWith(pair.Tabs);
        }
        return made;
    }
}
