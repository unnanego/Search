using System.Collections.ObjectModel;

namespace Search.Core;

// Everything a window knows about its tabs (Browser.swift, the part Phase 1 needs): which exist, which one is
// showing, and what was closed so Ctrl+Shift+T can bring it back. Bench tabs live in the same row, at its end, but
// are never chosen for you and never counted by the keys that walk the row.
public sealed class Browser : Observable
{
    private readonly Stack<(Uri? Url, string Title, string? Name, int Index, bool Private)> _closed = new();
    private Tab? _selected;

    public ObservableCollection<Tab> Tabs { get; } = new();
    /// The WebView2 profile new tabs here take: the space on screen's own sign-ins, or null for the default.
    public string? Store { get; set; }
    /// Pages shown side by side (Split.swift): two to four tabs in one place, the one with the keys being Selected.
    public List<SplitPages> Splits { get; } = new();
    public event Action? SplitsChanged;
    /// Named sections of the row (TabGroup.swift); a tab's GroupId says which one it's in.
    public List<TabGroup> Groups { get; } = new();
    /// The pins or the groups changed: the column draws itself again.
    public event Action? RowShapeChanged;
    public IEnumerable<Tab> Ordinary => Tabs.Where(t => !t.Bench);
    public bool CanReopen => _closed.Count > 0;

    /// Something the session keeps changed: the row, the tab in front, or a tab's address or name.
    public event Action? SessionChanged;

    public Tab? Selected
    {
        get => _selected;
        set
        {
            if (value is { Bench: true } || _selected == value) return;
            if (_selected is { } left) left.Touched = DateTimeOffset.UtcNow;
            Set(ref _selected, value);
            SessionChanged?.Invoke();
        }
    }

    public Tab Open(Uri? url, bool select = true, Tab? after = null, bool bench = false, bool @private = false, bool agent = false)
    {
        bench |= agent;
        var tab = new Tab { Bench = bench, Agent = agent, Private = @private, Store = bench || @private ? null : Store };
        if (url is not null) tab.Url = url;
        // Bench tabs go at the very end; a tab opened from another goes beside it; anything else ends the ordinary row.
        var at = bench ? Tabs.Count : after is not null && Tabs.IndexOf(after) is var i and >= 0 ? i + 1 : Ordinary.Count();
        Insert(tab, at);
        if (select && !bench) Selected = tab;
        return tab;
    }

    /// A tab built elsewhere (the session, a page's new window) put in the row.
    public void Insert(Tab tab, int at)
    {
        Tabs.Insert(Math.Clamp(at, 0, Tabs.Count), tab);
        tab.PropertyChanged += (_, e) => { if (!tab.Bench && e.PropertyName is nameof(Tab.Url) or nameof(Tab.Name)) SessionChanged?.Invoke(); };
        if (!tab.Bench) SessionChanged?.Invoke();
    }

    public void Close(Tab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        if (!tab.Bench) _closed.Push((tab.Url, tab.Title, tab.Name, index, tab.Private));
        Unsplit(tab);
        var group = tab.GroupId;
        if (Selected == tab)
        {
            // The neighbour on the right, as the row reads, else the one on the left.
            var row = Ordinary.ToList();
            var at = row.IndexOf(tab);
            _selected = null;
            Selected = row.Count > 1 ? row[at + 1 < row.Count ? at + 1 : at - 1] : null;
        }
        Tabs.RemoveAt(index);
        if (group is { } emptied && !Tabs.Any(t => t.GroupId == emptied)) { Groups.RemoveAll(g => g.Id == emptied); RowShapeChanged?.Invoke(); }
        if (!tab.Bench) SessionChanged?.Invoke();
        Changed(nameof(CanReopen));
    }

    public Tab? Reopen()
    {
        if (!_closed.TryPop(out var gone)) return null;
        var tab = new Tab { Private = gone.Private, Title = gone.Title, Name = gone.Name, Store = gone.Private ? null : Store };
        if (gone.Url is not null) tab.Url = gone.Url;
        Insert(tab, Math.Min(gone.Index, Ordinary.Count()));
        Selected = tab;
        Changed(nameof(CanReopen));
        return tab;
    }

    public Tab Duplicate(Tab tab) { var copy = Open(tab.Url, select: true, after: tab, @private: tab.Private); copy.Name = tab.Name; return copy; }

    /// Ctrl+1 to Ctrl+8 go to that tab, Ctrl+9 to the last, as in every browser.
    public void SelectNumber(int n)
    {
        var row = Ordinary.ToList();
        if (row.Count == 0) return;
        Selected = n >= 9 ? row[^1] : n - 1 < row.Count ? row[n - 1] : Selected;
    }

    /// Ctrl+Tab and Ctrl+Shift+Tab: along the row, round from the end to the start.
    public void Step(int by)
    {
        var row = Ordinary.ToList();
        if (row.Count == 0) return;
        var at = Selected is { } s ? row.IndexOf(s) : -1;
        Selected = row[((at + by) % row.Count + row.Count) % row.Count];
    }

    /// Pinned: kept at the top of the row, with the page it is now as its home and a letter from the site. A page in a
    /// split brings the rest of it along when every page of it can be a pin: pinned together, always as rows, and still
    /// one split (Browser.pin). Otherwise it leaves the split and is pinned alone.
    public void Pin(Tab tab)
    {
        if (tab.Pinned || tab.Bench || tab.Private) return;
        if (PinnableSplit(tab) is { } members) { PinTogether(members, Guid.NewGuid()); return; }
        MakePin(tab);
        Unsplit(tab);
        tab.Listed = false;
        tab.Folder = null;
        // A new pin is a square, after the other squares and ahead of the pinned rows.
        var last = Tabs.Where(t => t.Pinned && !t.Listed && t != tab).Select(Tabs.IndexOf).DefaultIfEmpty(-1).Max();
        var from = Tabs.IndexOf(tab);
        Tabs.Move(from, Math.Min(last + (from > last ? 1 : 0), Tabs.Count - 1));
        RowShaped();
    }

    private static void MakePin(Tab tab) { tab.PinId = Guid.NewGuid(); tab.Home = tab.Url; tab.Letter = Pins.Monogram(tab.Url, tab.Title); tab.GroupId = null; }

    /// A split pinned whole: each page a pinned row, after the other rows and in the split's order, in no folder, all
    /// naming one pair, which pins.json keeps so every window shows them as one split.
    private void PinTogether(IReadOnlyList<Tab> members, Guid pair)
    {
        foreach (var member in members)
        {
            if (!member.Pinned) MakePin(member);
            (member.Listed, member.Folder, member.PinPair) = (true, null, pair);
            var last = Tabs.Where(t => t.Pinned && t != member).Select(Tabs.IndexOf).DefaultIfEmpty(-1).Max();
            var from = Tabs.IndexOf(member);
            Tabs.Move(from, Math.Min(last + (from > last ? 1 : 0), Tabs.Count - 1));
        }
        SplitsChanged?.Invoke();
        RowShaped();
    }

    /// The pages of the tab's split, when every one of them could be a pin: web pages, none private, none pinned yet.
    public IReadOnlyList<Tab>? PinnableSplit(Tab tab) => SplitOf(tab) is { } split && split.Tabs.All(t => !t.Pinned && !t.Bench && !t.Private && t.Url is { Scheme: "http" or "https" }) ? split.Tabs : null;

    /// The split a pin is in with other pins alone: a split pinned whole.
    public SplitPages? PinnedPair(Tab tab) => tab.Pinned && SplitOf(tab) is { } split && split.Tabs.All(t => t.Pinned) ? split : null;

    /// The splits pinned whole as pins.json has them (PinDef.Pair), for a row whose pins were just matched to it: pins
    /// naming one pair, two to four of them, next to each other and in no other split, made one; and a split of pins
    /// alone that pins.json no longer holds together let go. So a split pinned in one window is one in the others.
    public void KeepPinnedPairs()
    {
        var changed = Splits.RemoveAll(s => s.Tabs.All(t => t.Pinned) && (s.Tabs[0].PinPair is not { } id || s.Tabs.Any(t => t.PinPair != id))) > 0;
        foreach (var members in Tabs.Where(t => t is { Pinned: true, PinPair: not null } && SplitOf(t) is null).GroupBy(t => t.PinPair).Select(g => g.OrderBy(Tabs.IndexOf).ToList()))
        {
            var at = Tabs.IndexOf(members[0]);
            if (members.Count is < 2 or > SplitPages.Most || members.Where((t, i) => Tabs.IndexOf(t) != at + i).Any()) continue;
            var split = new SplitPages(SplitAxis.Horizontal);
            split.Tabs.AddRange(members);
            split.Even();
            Splits.Add(split);
            changed = true;
        }
        if (changed) SplitsChanged?.Invoke();
    }

    /// Show as Row / Show as Square: the pin keeps its letter, page and home, and goes to the end of the squares or
    /// the rows. A square is in no folder. A split pinned whole stays rows.
    public void SetListed(Tab tab, bool listed)
    {
        if (!tab.Pinned || tab.Listed == listed || PinnedPair(tab) is not null) return;
        tab.Listed = listed;
        if (!listed) tab.Folder = null;
        var others = Tabs.Where(t => t != tab && t.Pinned).ToList();
        var at = listed ? others.Count : others.Count(t => !t.Listed);
        var place = at == 0 ? 0 : Tabs.IndexOf(others[at - 1]) + 1;
        var from = Tabs.IndexOf(tab);
        Tabs.Move(from, Math.Min(from < place ? place - 1 : place, Tabs.Count - 1));
        RowShaped();
    }

    /// Out of the pins, to the head of the loose tabs. A split pinned whole comes out whole, still a split, its pages
    /// in their order.
    public void Unpin(Tab tab)
    {
        if (!tab.Pinned) return;
        var members = PinnedPair(tab)?.Tabs.ToList() ?? [tab];
        foreach (var member in members) (member.PinId, member.Home, member.Letter, member.Listed, member.Folder, member.PinPair) = (null, null, null, false, null, null);
        // The pins are the head of the row.
        var head = Tabs.Count(t => t.Pinned);
        for (var k = 0; k < members.Count; k++) { var to = Math.Min(head + k, Tabs.Count - 1); if (Tabs.IndexOf(members[k]) is var from and >= 0 && from != to) Tabs.Move(from, to); }
        RowShaped();
    }

    /// A new group holding this tab; or the tab into a group already there, next to its other tabs.
    public TabGroup Group(Tab tab, TabGroup? into = null, string name = "")
    {
        if (into is null) { into = new TabGroup(Guid.NewGuid(), name.Length > 0 ? name : TabGroups.NewName(Groups)); Groups.Add(into); }
        tab.GroupId = into.Id;
        var members = Tabs.Where(t => t.GroupId == into.Id && t != tab).ToList();
        if (members.Count > 0) { var at = Tabs.IndexOf(members[^1]); var from = Tabs.IndexOf(tab); Tabs.Move(from, from < at ? at : at + 1); }
        RowShaped();
        return into;
    }

    public void Ungroup(Tab tab)
    {
        if (tab.GroupId is not { } id) return;
        tab.GroupId = null;
        if (!Tabs.Any(t => t.GroupId == id)) Groups.RemoveAll(g => g.Id == id);
        RowShaped();
    }

    public void SetGroup(TabGroup group)
    {
        var at = Groups.FindIndex(g => g.Id == group.Id);
        if (at < 0) return;
        Groups[at] = group;
        RowShaped();
    }

    private void RowShaped() { RowShapeChanged?.Invoke(); SessionChanged?.Invoke(); }

    public SplitPages? SplitOf(Tab? tab) => tab is null ? null : Splits.FirstOrDefault(s => s.Tabs.Contains(tab));

    /// The second page beside the first, or one more in the first's split (up to four). The new page has the keys.
    public SplitPages? Split(Tab with, Tab other, SplitAxis axis = SplitAxis.Horizontal)
    {
        if (with == other || with.Bench || other.Bench) return null;
        Unsplit(other);
        // Two pins put side by side stay pins, as a split pinned whole: rows, in no folder, naming one pair.
        if (with.Pinned && other.Pinned && (SplitOf(with) is null || PinnedPair(with) is { Tabs.Count: < SplitPages.Most }))
        {
            var pair = PinnedPair(with) is not null && with.PinPair is { } kept ? kept : Guid.NewGuid();
            SetListed(other, true);
            SetListed(with, true);
            foreach (var pin in new[] { with, other }) (pin.Folder, pin.PinPair) = (null, pair);
        }
        var split = SplitOf(with);
        if (split is null) { split = new SplitPages(axis); split.Tabs.Add(with); Splits.Add(split); }
        else if (split.Tabs.Count >= SplitPages.Most) return null;
        split.Tabs.Add(other);
        split.Even();
        // Kept next to each other in the row, the split's place being its first page's.
        var at = Tabs.IndexOf(split.Tabs[^2]) + 1;
        if (Tabs.IndexOf(other) is var from and >= 0 && from != at) Tabs.Move(from, from < at ? at - 1 : at);
        Selected = other;
        SplitsChanged?.Invoke();
        SessionChanged?.Invoke();
        return split;
    }

    /// A page out of its split; a split left with one page is no split.
    public void Unsplit(Tab tab)
    {
        if (SplitOf(tab) is not { } split) return;
        split.Tabs.Remove(tab);
        if (split.Tabs.Count < 2) Splits.Remove(split); else split.Even();
        SplitsChanged?.Invoke();
        SessionChanged?.Invoke();
    }

    /// Tabs › Separate Split Tabs: every page its own tab again.
    public void Separate(SplitPages split) { Splits.Remove(split); SplitsChanged?.Invoke(); SessionChanged?.Invoke(); }

    /// The keys to the next page of the split on screen (Ctrl+Alt+→ / ←).
    public void StepPane(int by)
    {
        if (SplitOf(Selected) is not { } split) return;
        var at = split.Tabs.IndexOf(Selected!);
        Selected = split.Tabs[((at + by) % split.Tabs.Count + split.Tabs.Count) % split.Tabs.Count];
    }

    public void Resized(SplitPages split) { SplitsChanged?.Invoke(); SessionChanged?.Invoke(); }

    public void Move(Tab tab, int to)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0 || tab.Bench) return;
        Tabs.Move(from, Math.Clamp(to, 0, Ordinary.Count() - 1));
        SessionChanged?.Invoke();
    }
}

/// Two to four tabs shown side by side (Split.swift): their order, the axis, and how much of the page area each has.
public sealed class SplitPages(SplitAxis axis)
{
    public const int Most = 4;
    public List<Tab> Tabs { get; } = new();
    public SplitAxis Axis { get; set; } = axis;
    /// Fractions of the page area, adding up to one.
    public List<double> Sizes { get; } = new();

    public void Even() { Sizes.Clear(); Sizes.AddRange(Enumerable.Repeat(1.0 / Tabs.Count, Tabs.Count)); }

    /// The divider after page `index` dragged to `fraction` of the whole; each page keeps at least a tenth.
    public void Drag(int index, double fraction)
    {
        var before = Sizes.Take(index).Sum();
        var pair = Sizes[index] + Sizes[index + 1];
        var mine = Math.Clamp(fraction - before, 0.1, pair - 0.1);
        Sizes[index] = mine;
        Sizes[index + 1] = pair - mine;
    }
}