using Search.Core;

namespace Search.App;

// Pinned tabs are the same in every window (Pins.swift): pinned, unpinned, moved or renamed in one, so in the others.
// Each window holds its own tab for each pin, its own page wherever you took it; only what makes the pin a pin is
// shared, in pins.json (Pins.cs). A window says what its row's pins are whenever its row changes, and follows when
// another window says they changed: a pin new to it is a tab of its own at the pin's page, asleep until shown.
// Following is quiet, and so is writing the row down: a window only ever tells of a change made in it, or two windows
// whose pins' pages have different titles would answer each other for ever.
public sealed partial class MainWindow
{
    private Pins Pinned => App.Shared.Pins;

    private void WirePins() => Hook<Action<PinsChange>>(h => Pinned.Changed += h, h => Pinned.Changed -= h, change =>
    {
        if (ReferenceEquals(change.From, this) || change.Space != _space) return;
        DispatcherQueue.TryEnqueue(FollowPins);
    });

    /// A row as last written, with the space's pins as they are now: what Restore reads.
    private Session.Shape WithPins(Session.Shape shape, Guid space) => Pins.InSession(shape, Pinned.Defs(space));

    private static PinnedTab Facts(Tab tab) => new(tab.Id)
    {
        Pin = tab.Pinned ? tab.Letter ?? Pins.Monogram(tab.Url, tab.Title) : null, PinId = tab.PinId, Listed = tab.Pinned && tab.Listed, Folder = tab.Pinned && tab.Listed ? tab.Folder : null, Pair = tab.Pinned ? tab.PinPair : null, Home = tab.Home?.AbsoluteUri, Url = tab.Url?.AbsoluteUri, Title = tab.Title, Name = tab.Name, Private = tab.Private
    };

    /// The row changed: its pins, as they now are, told to the other windows (nothing is said when they're the same).
    private void TellPins()
    {
        if (_retired || _following) return;
        var (defs, _) = Pins.Defs(_browser.Ordinary.Where(t => !t.Bench).Select(Facts).ToList());
        Pinned.Set(_space, defs, from: this);
    }

    private bool _following;

    /// Another window changed this space's pins: this row takes them, keeping its own tab for each pin it has.
    private void FollowPins()
    {
        if (_retired) return;
        var tabs = _browser.Ordinary.ToList();
        var (row, gone) = Pins.Reconcile(tabs.Select(Facts).ToList(), Pinned.Defs(_space), new HashSet<Guid>(), _prefs.ShowsPinFolders ? App.Shared.Folders.Folders(_space) : null);
        _following = true;
        try
        {
            foreach (var id in gone) if (tabs.FirstOrDefault(t => t.Id == id) is { } tab) { if (_browser.Selected == tab) _browser.Selected = _browser.Ordinary.FirstOrDefault(t => !t.Pinned); _browser.Close(tab); }
            for (var at = 0; at < row.Count; at++)
            {
                var facts = row[at];
                var tab = facts.Fresh ? new Tab { Title = facts.Title, Store = _browser.Store } : tabs.FirstOrDefault(t => t.Id == facts.Id);
                if (tab is null) continue;
                if (facts.IsPin) { tab.PinId = facts.PinId; tab.Letter = facts.Pin; tab.Name = facts.Name; tab.Home = Uri.TryCreate(facts.Home, UriKind.Absolute, out var home) ? home : null; tab.Listed = facts.Listed; tab.Folder = facts.Folder; tab.PinPair = facts.Pair; }
                if (facts.Fresh) { if (tab.Home is { } page) tab.Url = page; _browser.Insert(tab, Math.Min(at, _browser.Tabs.Count)); }
                else if (_browser.Tabs.IndexOf(tab) is var from and >= 0 && from != at && at < _browser.Tabs.Count) _browser.Move(tab, at);
            }
            _row.Sync();
        }
        finally { _following = false; }
        if (!_browser.Ordinary.Any()) NewTab();
        SaveSession(now: false);
    }
}
