using Microsoft.UI.Dispatching;
using Search.Core;

namespace Search.App;

// Tabs you aren't using (Sleep.swift, Closing.swift, Archive.swift). Put to sleep after half an hour, their web view let
// go of (about 28 MB each, the spike measured) and the address, title and scroll kept, so the page comes back where it
// was when you go to it. Closed by themselves after a while, if you turn that on, into the archive, where they wait for
// thirty days. What's playing, loading, on screen, pinned, named or in a group is left alone, as the rules say.
public sealed partial class MainWindow
{
    private readonly Archive _archive;
    private DispatcherQueueTimer? _idle;
    private readonly DateTimeOffset _launched = DateTimeOffset.UtcNow;

    private bool SleepOn { get => !App.Notes.TryGetValue(Sleep.OnKey, out var on) || on != "false"; set => App.Notes[Sleep.OnKey] = value ? "true" : "false"; }
    private SleepPace SleepPace { get => Sleep.Pace(App.Notes.TryGetValue(Sleep.PaceKey, out var pace) ? pace : null); set => App.Notes[Sleep.PaceKey] = Sleep.Raw(value); }
    private TabLife TabLife { get => App.Notes.TryGetValue(Closing.Key, out var hours) && long.TryParse(hours, out var h) ? Closing.Life(h) : TabLife.Never; set => App.Notes[Closing.Key] = ((int)value).ToString(); }
    /// A test run may make the waits seconds long (SEARCH_IDLE_SECONDS), to see a tab sleep without waiting half an hour.
    private static double IdleSeconds => Store.Testing && double.TryParse(Environment.GetEnvironmentVariable("SEARCH_IDLE_SECONDS"), out var s) ? s : 0;

    private void WireIdle()
    {
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = IdleSeconds > 0 ? TimeSpan.FromSeconds(1) : Sleep.Every(Sleep.After(SleepPace));
        _idle.Tick += (_, _) => _ = LookAtIdle();
        _idle.Start();
        // A page that wakes goes back to where it was scrolled.
        _pages.Loaded += tab => { if (tab.Scroll is { } at) { tab.Scroll = null; _ = _pages.Evaluate(tab, Sleep.RestoreScroll(new Slept(tab.Url!, tab.Title) { ScrollX = at.X, ScrollY = at.Y })); } };
    }

    /// Every tab the window has, the ones on screen and the ones set aside in other spaces.
    private IEnumerable<(Tab Tab, bool Parked)> EveryTab() => _browser.Ordinary.Select(t => (t, false)).Concat(_aside.Values.SelectMany(a => a.Tabs).Select(t => (t, true)));

    private IdleTab Facts(Tab tab, bool parked) => new(tab.Id)
    {
        // A tab never left yet counts from when it was first seen here, not from launch: one opened a moment ago isn't idle.
        Touched = tab.Touched ??= DateTimeOffset.UtcNow, OnScreen = !parked && _pages.OnScreen.Contains(tab), Named = tab.Name is not null, Private = tab.Private, Bench = tab.Bench,
        Blank = tab.Url is null, Parked = parked, Asleep = !tab.Live, HasPage = tab.Live, Loading = tab.Loading, Noisy = tab.Audible,
        Asking = _asking?.Tab == tab, Opened = _peekFrom == tab && _peek is not null
    };

    private async Task LookAtIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var all = EveryTab().ToList();
        if (SleepOn)
            foreach (var id in Sleep.Due(all.Select(t => Facts(t.Tab, t.Parked)), now, Sleep.After(SleepPace, seconds: IdleSeconds)))
                if (all.FirstOrDefault(t => t.Tab.Id == id).Tab is { Live: true } tab) await PutToSleep(tab);
        if (Closing.After(TabLife, IdleSeconds) is null) return;
        foreach (var together in Closing.Due(all.Select(t => Facts(t.Tab, t.Parked)).ToList(), [], now, TabLife, IdleSeconds))
            foreach (var id in together)
                if (all.FirstOrDefault(t => t.Tab.Id == id) is { Tab: not null } due) CloseIdle(due.Tab, due.Parked);
    }

    private async Task PutToSleep(Tab tab)
    {
        if (await _pages.Evaluate(tab, Sleep.ReadScroll) is System.Text.Json.Nodes.JsonArray { Count: 2 } at && at[0]?.GetValue<double>() is { } x && at[1]?.GetValue<double>() is { } y && (x > 0 || y > 0)) tab.Scroll = (x, y);
        // Its sessionStorage, for the web view that wakes it (Sleep.KeepSession); never a private tab's.
        var session = tab.Private ? null : await _pages.Evaluate(tab, Sleep.ReadSession) is System.Text.Json.Nodes.JsonValue read && read.TryGetValue<string>(out var json) ? json : null;
        // Checked again after asking the page: it may have been chosen, or started playing, meanwhile.
        if (_pages.OnScreen.Contains(tab) || tab.Audible || tab.Loading || !tab.Live) return;
        tab.KeptSession = Sleep.KeepSession(session, tab.Private);
        _pages.Sleep(tab);
    }

    /// Closed by itself: into the archive, out of the row (or out of its space, if that one isn't on screen).
    private void CloseIdle(Tab tab, bool parked)
    {
        if (Archive.Takes(tab.Url, tab.Private) is { } url) _archive.Add(url, tab.Title);
        if (!parked) { _browser.Close(tab); if (!_browser.Ordinary.Any()) NewTab(); return; }
        foreach (var (space, aside) in _aside.ToList())
            if (aside.Tabs.Remove(tab)) { _pages.Parked.Remove(tab); _pages.Sleep(tab); _aside[space] = aside; }
    }

    // The archive panel: what closed by itself, newest first; a click brings it back as a tab.
    private List<PanelRow> ArchiveRows(string words) =>
        _archive.Listed(words).Select(a => new PanelRow { Item = a, Glyph = "", Title = a.Label, Detail = $"{a.Site} · closed {When(a.Closed)}", ActionGlyph = "", ActionName = "Forget" }).ToList();

    private void Unarchive(Archived archived) { _archive.Remove(archived.Id); Go(archived.Url, newTab: true); }
}
