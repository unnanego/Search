using Microsoft.UI.Dispatching;
using Search.Core;

namespace Search.App;

// What was open last time (Session.swift): written a moment after the row changes and at once on the way out, read at
// launch. Restored tabs have no web view until they're shown; only the one in front loads. Private and bench tabs
// are never written. The oldest window's rows are the session files, as they always were; every other window's are
// its line in windows.json (Windows.swift), and when the oldest closes the next one takes the files over.
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _saving;

    private void Restore(Guid? space = null)
    {
        // Built a tab at a time: no window hears of its pins until it's whole.
        _following = true;
        try { RestoreRow(space); } finally { _following = false; }
    }

    /// A row read back: the session file's (or this window's line), with the space's pins as they are now. A row
    /// with nothing kept but the pins (a new window, a space never opened here) starts on an empty tab.
    private void RestoreRow(Guid? space)
    {
        _browser.Groups.Clear();
        var kept = _owner ? Session.Read(space ?? _space) : _record?.Rows.GetValueOrDefault(SwiftJson.Uuid(space ?? _space)) ?? Session.Shape.Empty;
        var shape = WithPins(kept, space ?? _space);
        // Which folder holds a pinned row is the pin's own, in pins.json, not the session's.
        var defs = Pinned.Defs(space ?? _space).ToDictionary(d => d.Id);
        foreach (var entry in shape.Tabs)
        {
            var tab = new Tab { Title = entry.Title, Name = entry.Name, Muted = entry.Muted, Touched = entry.Touched, Store = _browser.Store };
            if (entry.Pin is { } letter) { tab.PinId = entry.PinId ?? Guid.NewGuid(); tab.Letter = letter; tab.Home = Uri.TryCreate(entry.Home ?? entry.Url, UriKind.Absolute, out var home) ? home : null; tab.Listed = entry.Listed == true; if (defs.GetValueOrDefault(tab.PinId.Value) is { } def) { tab.Folder = def.Folder; tab.PinPair = def.Pair; } }
            else if (entry.GroupId is { } group && shape.Groups?.Any(g => g.Id == group) == true) tab.GroupId = group;
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)) tab.Url = url;
            _browser.Insert(tab, _browser.Ordinary.Count());        }
        if (shape.Groups is { } groups) foreach (var group in groups.Where(g => _browser.Ordinary.Any(t => t.GroupId == g.Id) && _browser.Groups.All(h => h.Id != g.Id))) _browser.Groups.Add(group);
        _row.Sync();
        var row = _browser.Ordinary.ToList();
        // Pages side by side come back side by side, when every page of them did.
        foreach (var split in shape.Splits.Where(s => s.Tabs.Count >= 2 && s.Tabs.All(i => i >= 0 && i < row.Count)))
        {
            var pages = new SplitPages(split.Axis);
            pages.Tabs.AddRange(split.Tabs.Select(i => row[i]).Distinct());
            if (pages.Tabs.Count < 2 || pages.Tabs.Any(t => _browser.SplitOf(t) is not null)) continue;
            pages.Sizes.AddRange(split.Sizes.Count == pages.Tabs.Count && split.Sizes.All(s => s > 0) ? split.Sizes.Select(s => s / split.Sizes.Sum()) : Enumerable.Repeat(1.0 / pages.Tabs.Count, pages.Tabs.Count));
            _browser.Splits.Add(pages);
        }
        // A fresh start marks no tab in front: an empty one opens beside what was kept.
        if (kept.Tabs.Count > 0 && row.Count > 0 && shape.Active >= 0 && shape.Active < row.Count) _browser.Selected = row[shape.Active];
        else NewTab();
    }

    private void SaveSession(bool now)
    {
        if (now) { _saving?.Stop(); Write(now: true); return; }
        _saving ??= DispatcherQueue.CreateTimer();
        _saving.Interval = TimeSpan.FromSeconds(1);
        _saving.IsRepeating = false;
        _saving.Tick -= OnSaveTick;
        _saving.Tick += OnSaveTick;
        _saving.Start();
    }

    private void OnSaveTick(DispatcherQueueTimer sender, object args) => Write(now: false);

    /// The row as it is now: to the session files if this window holds them, and windows.json either way.
    private void Write(bool now)
    {
        if (_retired) return;
        if (_owner) Session.Write(Snapshot(), now, space: _space);
        App.SaveWindows(now);
    }

    /// The window going, with others open (Windows.swift's retire): every page let go, nothing written from here on.
    public void Retire()
    {
        _retired = true;
        _saving?.Stop();
        AgentGone();
        foreach (var off in _unhook) off();
        _unhook.Clear();
        foreach (var little in _littles.ToList()) little.Close();
        foreach (var tab in EveryTab().Select(t => t.Tab).ToList()) _pages.Sleep(tab);
    }

    /// Closed with its profile: nothing kept, nothing written.
    public void Discard() { Discarded = true; _retired = true; Close(); }

    public bool Discarded { get; private set; }

    /// Its rows to their files now, before it goes.
    public void WriteNow() => SaveSession(now: true);

    /// The last window closing: the app is. Its rows written now, tabs and all.
    public void Shut() { SaveSession(now: true); _retired = true; AgentGone(); foreach (var off in _unhook) off(); _unhook.Clear(); }

    private bool _retired;

    /// The oldest window closed and this one is now the oldest: its rows become the session files, every space of
    /// its profile written now, an empty row where it has none (Browser.takeFiles).
    public void TakeFiles()
    {
        if (_owner) return;
        var rows = AllRows();
        _owner = true;
        foreach (var space in _spaces.Of(_spaces.ProfileOf(_space))) Session.Write(rows.GetValueOrDefault(SwiftJson.Uuid(space.Id)) ?? Session.Shape.Empty, now: true, space: space.Id);
    }

    /// This window as windows.json keeps it: where it is, which space it shows and, unless it's the oldest, its rows.
    public WindowRecord Record(bool rows) => new() { Id = _id, Frame = Frame(), Space = _space, Rows = rows ? AllRows() : new(), Folded = _prefs.Folded ? true : null };

    private Guid _id = Guid.NewGuid();

    /// Every row the window has: the space it shows, the ones set aside, and last time's for spaces not looked at yet.
    private Dictionary<string, Session.Shape> AllRows()
    {
        var rows = new Dictionary<string, Session.Shape>();
        if (_record is not null) foreach (var (key, shape) in _record.Rows) rows[key] = shape;
        foreach (var (space, aside) in _aside) rows[SwiftJson.Uuid(space)] = Snapshot(aside.Tabs, aside.Selected, aside.Splits, aside.Groups);
        rows[SwiftJson.Uuid(_space)] = Snapshot();
        foreach (var key in rows.Where(r => r.Value.Tabs.Count == 0).Select(r => r.Key).ToList()) rows.Remove(key);
        return rows;
    }

    /// Where the window is, as the Mac writes a frame ("{{x, y}, {width, height}}"), in this screen's pixels.
    private string Frame()
    {
        var (at, size) = (AppWindow.Position, AppWindow.Size);
        return "{{" + at.X + ", " + at.Y + "}, {" + size.Width + ", " + size.Height + "}}";
    }

    private static (int X, int Y, int Width, int Height)? FrameOf(string? frame)
    {
        var numbers = (frame ?? "").Split(['{', '}', ',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(n => double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int?)d : null).ToList();
        return numbers.Count == 4 && numbers.All(n => n is not null) && numbers[2] > 100 && numbers[3] > 100 ? (numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value, numbers[3]!.Value) : null;
    }

    /// Where it was last time, or down and to the right of the window it came from, as new windows go.
    private void Place(WindowRecord? record, MainWindow? from)
    {
        if (record is not null) _id = record.Id;
        if (FrameOf(record?.Frame) is { } frame) { AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(frame.X, frame.Y, frame.Width, frame.Height)); return; }
        if (from is not null) { var (at, size) = (from.AppWindow.Position, from.AppWindow.Size); AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(at.X + 32, at.Y + 32, size.Width, size.Height)); return; }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
    }

    private Session.Shape Snapshot() => Snapshot(_browser.Ordinary.ToList(), _browser.Selected, _browser.Splits, _browser.Groups);

    private static Session.Shape Snapshot(IReadOnlyList<Tab> row, Tab? selected, List<SplitPages> allSplits, List<TabGroup> allGroups)
    {
        var kept = row.Where(t => !t.Private && t.Url is not null).ToList();
        var entries = kept.Select(t => new Session.Entry(t.Url!.AbsoluteUri, t.Title)
        {
            Name = t.Name, Muted = t.Muted, Touched = t.Touched, Pin = t.Letter, PinId = t.PinId, Home = t.Home?.AbsoluteUri, Listed = t.Pinned ? t.Listed : null, GroupId = t.Pinned ? null : t.GroupId
        }).ToList();
        // A private tab in front isn't written; the nearest kept tab before it is marked in front instead.
        var front = selected is null ? -1 : row.ToList().IndexOf(selected);
        while (front >= 0 && !kept.Contains(row[front])) front--;
        var splits = allSplits.Where(s => s.Tabs.All(kept.Contains))
            .Select(s => new Session.Split(s.Tabs.Select(t => kept.IndexOf(t)).ToList(), s.Sizes.ToList()) { Axis = s.Axis, Focused = selected is not null && s.Tabs.Contains(selected) ? kept.IndexOf(selected) : null }).ToList();
        var groups = allGroups.Where(g => kept.Any(t => t.GroupId == g.Id)).ToList();
        return new Session.Shape(entries, front >= 0 ? kept.IndexOf(row[front]) : kept.Count > 0 && selected is not null ? 0 : -1) { Splits = splits, Groups = groups.Count > 0 ? groups : null };
    }

    private SearchEngine Engine => SearchEngines.FromId(_prefs.Engine) ?? SearchEngines.Standard;
    private string SearchName => Engine.Name(_prefs.CustomEngine);
    private Uri SearchFor(string words) => Engine.SearchUrl(words, _prefs.CustomEngine) ?? SearchEngines.Standard.SearchUrl(words)!;
    private static List<(string Id, string Title)> SearchChoices() => SearchEngines.All.Select(e => (e.Id(), e.Title())).ToList();
}
