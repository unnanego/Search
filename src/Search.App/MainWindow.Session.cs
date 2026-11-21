using Microsoft.UI.Dispatching;
using Search.Core;

namespace Search.App;

// What was open last time (Session.swift): written a moment after the row changes and at once on the way out, read at
// launch. Restored tabs have no web view until they're shown; only the one in front loads. Private and bench tabs
// are never written.
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _saving;

    private void Restore(Guid? space = null)
    {
        _browser.Groups.Clear();
        var shape = Session.Read(space ?? _space);
        foreach (var entry in shape.Tabs)
        {
            var tab = new Tab { Title = entry.Title, Name = entry.Name, Muted = entry.Muted, Touched = entry.Touched, Store = _browser.Store };
            if (entry.Pin is { } letter) { tab.PinId = entry.PinId ?? Guid.NewGuid(); tab.Letter = letter; tab.Home = Uri.TryCreate(entry.Home ?? entry.Url, UriKind.Absolute, out var home) ? home : null; }
            else if (entry.GroupId is { } group && shape.Groups?.Any(g => g.Id == group) == true) tab.GroupId = group;
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)) tab.Url = url;
            _browser.Insert(tab, _browser.Ordinary.Count());
        }
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
        if (row.Count == 0) NewTab();
        else if (shape.Active >= 0 && shape.Active < row.Count) _browser.Selected = row[shape.Active];
        // A fresh start marks no tab in front: an empty one opens beside what was kept.
        else NewTab();
    }

    private void SaveSession(bool now)
    {
        if (now) { _saving?.Stop(); Session.Write(Snapshot(), now: true, space: _space); return; }
        _saving ??= DispatcherQueue.CreateTimer();
        _saving.Interval = TimeSpan.FromSeconds(1);
        _saving.IsRepeating = false;
        _saving.Tick -= OnSaveTick;
        _saving.Tick += OnSaveTick;
        _saving.Start();
    }

    private void OnSaveTick(DispatcherQueueTimer sender, object args) => Session.Write(Snapshot(), space: _space);

    private Session.Shape Snapshot()
    {
        var kept = _browser.Ordinary.Where(t => !t.Private && t.Url is not null).ToList();
        var entries = kept.Select(t => new Session.Entry(t.Url!.AbsoluteUri, t.Title)
        {
            Name = t.Name, Muted = t.Muted, Touched = t.Touched, Pin = t.Letter, PinId = t.PinId, Home = t.Home?.AbsoluteUri, Listed = t.Pinned ? false : null, GroupId = t.Pinned ? null : t.GroupId
        }).ToList();
        // A private tab in front isn't written; the nearest kept tab before it is marked in front instead.
        var row = _browser.Ordinary.ToList();
        var front = _browser.Selected is { } s ? row.IndexOf(s) : -1;
        while (front >= 0 && !kept.Contains(row[front])) front--;
        var splits = _browser.Splits.Where(s => s.Tabs.All(kept.Contains))
            .Select(s => new Session.Split(s.Tabs.Select(t => kept.IndexOf(t)).ToList(), s.Sizes.ToList()) { Axis = s.Axis, Focused = s.Tabs.Contains(_browser.Selected!) ? kept.IndexOf(_browser.Selected!) : null }).ToList();
        var groups = _browser.Groups.Where(g => kept.Any(t => t.GroupId == g.Id)).ToList();
        return new Session.Shape(entries, front >= 0 ? kept.IndexOf(row[front]) : kept.Count > 0 && _browser.Selected is not null ? 0 : -1) { Splits = splits, Groups = groups.Count > 0 ? groups : null };
    }

    private SearchEngine Engine => SearchEngines.FromId(_prefs.Engine) ?? SearchEngines.Standard;
    private string SearchName => Engine.Name(_prefs.CustomEngine);
    private Uri SearchFor(string words) => Engine.SearchUrl(words, _prefs.CustomEngine) ?? SearchEngines.Standard.SearchUrl(words)!;
    private static List<(string Id, string Title)> SearchChoices() => SearchEngines.All.Select(e => (e.Id(), e.Title())).ToList();
}
