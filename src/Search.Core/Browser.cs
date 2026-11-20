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

    public Tab Open(Uri? url, bool select = true, Tab? after = null, bool bench = false, bool @private = false)
    {
        var tab = new Tab { Bench = bench, Private = @private };
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
        if (Selected == tab)
        {
            // The neighbour on the right, as the row reads, else the one on the left.
            var row = Ordinary.ToList();
            var at = row.IndexOf(tab);
            _selected = null;
            Selected = row.Count > 1 ? row[at + 1 < row.Count ? at + 1 : at - 1] : null;
        }
        Tabs.RemoveAt(index);
        if (!tab.Bench) SessionChanged?.Invoke();
        Changed(nameof(CanReopen));
    }

    public Tab? Reopen()
    {
        if (!_closed.TryPop(out var gone)) return null;
        var tab = new Tab { Private = gone.Private, Title = gone.Title, Name = gone.Name };
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

    public void Move(Tab tab, int to)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0 || tab.Bench) return;
        Tabs.Move(from, Math.Clamp(to, 0, Ordinary.Count() - 1));
        SessionChanged?.Invoke();
    }
}
