using Microsoft.UI.Dispatching;
using Search.Core;

namespace Search.App;

// What was open last time (Session.swift): written a moment after the row changes and at once on the way out, read at
// launch. Restored tabs have no web view until they're shown; only the one in front loads. Private and bench tabs
// are never written.
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _saving;

    private void Restore()
    {
        var shape = Session.Read();
        foreach (var entry in shape.Tabs)
        {
            var tab = new Tab { Title = entry.Title, Name = entry.Name, Muted = entry.Muted, Touched = entry.Touched };
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)) tab.Url = url;
            _browser.Insert(tab, _browser.Ordinary.Count());
        }
        var row = _browser.Ordinary.ToList();
        if (row.Count == 0) NewTab();
        else if (shape.Active >= 0 && shape.Active < row.Count) _browser.Selected = row[shape.Active];
        // A fresh start marks no tab in front: an empty one opens beside what was kept.
        else NewTab();
        _browser.SessionChanged += () => SaveSession(now: false);
    }

    private void SaveSession(bool now)
    {
        if (now) { _saving?.Stop(); Session.Write(Snapshot(), now: true); return; }
        _saving ??= DispatcherQueue.CreateTimer();
        _saving.Interval = TimeSpan.FromSeconds(1);
        _saving.IsRepeating = false;
        _saving.Tick -= OnSaveTick;
        _saving.Tick += OnSaveTick;
        _saving.Start();
    }

    private void OnSaveTick(DispatcherQueueTimer sender, object args) => Session.Write(Snapshot());

    private Session.Shape Snapshot()
    {
        var kept = _browser.Ordinary.Where(t => !t.Private && t.Url is not null).ToList();
        var entries = kept.Select(t => new Session.Entry(t.Url!.AbsoluteUri, t.Title) { Name = t.Name, Muted = t.Muted, Touched = t.Touched }).ToList();
        // A private tab in front isn't written; the nearest kept tab before it is marked in front instead.
        var row = _browser.Ordinary.ToList();
        var front = _browser.Selected is { } s ? row.IndexOf(s) : -1;
        while (front >= 0 && !kept.Contains(row[front])) front--;
        return new Session.Shape(entries, front >= 0 ? kept.IndexOf(row[front]) : kept.Count > 0 && _browser.Selected is not null ? 0 : -1);
    }

    private SearchEngine Engine => SearchEngines.FromId(_prefs.Engine) ?? SearchEngines.Standard;
    private string SearchName => Engine.Name(_prefs.CustomEngine);
    private Uri SearchFor(string words) => Engine.SearchUrl(words, _prefs.CustomEngine) ?? SearchEngines.Standard.SearchUrl(words)!;
    private static List<(string Id, string Title)> SearchChoices() => SearchEngines.All.Select(e => (e.Id(), e.Title())).ToList();
}
