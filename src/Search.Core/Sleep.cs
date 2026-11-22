using System.Text.Json;

namespace Search.Core;

// Tabs you aren't using, put to sleep (Sleep.swift).
//
// A page open in a tab keeps its whole browser process for as long as the tab exists: twenty tabs is gigabytes spent
// on the nineteen nobody is looking at. So a tab left alone for half an hour gives its page back (on Windows its
// WebView2 is closed: suspending one frees next to nothing) and keeps what it takes to come back where it was (Slept).
//
// Some tabs never sleep, because waking them couldn't give back what they were doing: the one on screen, pinned tabs
// (put down by hand, with Ctrl+W), a tab playing sound, on a call, sending a download, holding its video out, with a
// question waiting, the one the page on screen came from, or holding something typed and not sent (asked of the page
// by the window, last). A pin stays awake only in the space on screen: in one you aren't in it sleeps after the same
// wait as the tabs beside it, unless its site may send notifications. With the last window closed and the app still
// running, nothing is on screen: the tabs the same rules let go sleep at once (Shut).
//
// Half an hour is Normal; Later is two hours, Sooner ten minutes, and saving power never waits longer than Sooner. When
// memory is short the wait shrinks, whatever was chosen: to five minutes on a warning, to nothing when it is critical.
//
// Everything here is pure: the window hands in IdleTab facts and `now` on a timer (Every) and sleeps what comes back,
// one after another, looking again (StillIdle) before each.

/// How soon tabs sleep, as chosen in Settings: how much a waking tab bothers someone, not how many minutes.
public enum SleepPace { Later, Normal, Sooner }

/// What the sleeping and closing rules need of a tab, read when they run. The busy part (KeptAwake to Opened) is what
/// the page is in the middle of that letting it go would cut off, and is asked by both.
public sealed record IdleTab(Guid Id)
{
    /// When you last looked at it (left it, for the one you were on).
    public DateTimeOffset Touched { get; init; }
    /// When it was last let go of: unpinned, taken out of its group, its name taken away (Closing only).
    public DateTimeOffset? LetGo { get; init; }
    /// On screen: the tab in front, or a page of the split in front.
    public bool OnScreen { get; init; }
    /// On screen in another window, as the same tab there (Same tabs in every window; Closing only).
    public bool OnScreenElsewhere { get; init; }
    public bool Pinned { get; init; }
    /// Given a name by hand (Closing only).
    public bool Named { get; init; }
    /// Has a group id at all (Closing only: a tab in a group is one you are keeping).
    public bool Grouped { get; init; }
    public bool Private { get; init; }
    public bool Bench { get; init; }
    /// Has never been anywhere.
    public bool Blank { get; init; }
    /// In the row of a space that isn't on screen (a parked row).
    public bool Parked { get; init; }
    /// Its site may send notifications (allowed, and notifications on in Settings): a pin in a parked row stays awake for it.
    public bool MayNotify { get; init; }
    /// Already asleep, or restored and not yet opened: no web view behind it.
    public bool Asleep { get; init; }
    /// Has a web view (false for a tab that never built one).
    public bool HasPage { get; init; } = true;
    public bool Loading { get; init; }

    /// Its site is kept awake from its card (Sleep.KeepsAwake).
    public bool KeptAwake { get; init; }
    /// Something on the page is making noise.
    public bool Noisy { get; init; }
    /// Its video is out in the little window (picture in picture).
    public bool VideoOut { get; init; }
    /// The camera or the microphone is on.
    public bool OnCall { get; init; }
    public bool RecordingScreen { get; init; }
    public bool Downloading { get; init; }
    /// A dialog of the page's, or a permission question, is waiting.
    public bool Asking { get; init; }
    /// The page on screen was opened from it (a sign-in window hands its answer back to it).
    public bool Opened { get; init; }
}

/// What a slept tab keeps to come back where it was: its address and title, where it was scrolled to, and a picture
/// of it to show while the page is built again. The Mac also hands its back list to the new view; a new WebView2
/// can't be given one, so Back starts again from the page.
public sealed record Slept(Uri Url, string Title)
{
    public double ScrollX { get; init; }
    public double ScrollY { get; init; }
    /// The page as it last looked, compressed, at the screen's pixels.
    public byte[]? Picture { get; init; }
}

public static class Sleep
{
    /// Settings' keys, as the Mac's (Prefs.swift): on unless turned off, the pace, kept-awake sites on, and the sites.
    public const string OnKey = "tabs.sleep", PaceKey = "tabs.sleep.pace", AwakeSitesKey = "tabs.awakeSites";

    /// What a page is asked for before it goes, and given back once it is built again.
    public const string ReadScroll = "[window.scrollX, window.scrollY]";
    public static string RestoreScroll(Slept slept) => FormattableString.Invariant($"window.scrollTo({slept.ScrollX}, {slept.ScrollY})");

    // A tab keeps its sessionStorage through sleep (KeptSession in Sleep.swift). The web view built to wake it starts a
    // new browsing session, and sessionStorage goes with the old one, so a draft, a form half filled or a step of a
    // checkout some sites keep only there was lost. WebView2 has no way to hand it from one view to the next, so the
    // page's own is read as it falls asleep and written back as it wakes, before any of its scripts run. The page
    // itself only (frames inside it lose theirs, as before); not for a private tab, so nothing of one is held after its
    // view goes; not past SessionCap, since sleep is there to give memory back. Read and written in Search's world,
    // where a page can't stand in for the functions used: storage is the frame's, the same in every world.

    /// Past this, a page's storage isn't kept: half a megabyte, as the Mac's.
    public const int SessionCap = 512 * 1024;

    /// What the page is asked for before it goes: `[origin, [[key, value], …]]` as JSON, or null when it has nothing.
    public const string ReadSession = """
        (() => {
          try {
            const s = sessionStorage;
            if (!s.length) return null;
            const items = [];
            for (let i = 0; i < s.length; i++) { const k = s.key(i); items.push([k, s.getItem(k)]); }
            return JSON.stringify([location.origin, items]);
          } catch (e) { return null; }
        })()
        """;

    /// What a tab keeps of what ReadSession answered: nothing for a private tab, past the cap, or not in its shape (it
    /// goes into a script as it is, so only JSON of that shape ever does).
    public static string? KeepSession(string? read, bool @private) => !@private && read is { Length: > 0 } && System.Text.Encoding.UTF8.GetByteCount(read) <= SessionCap && Shaped(read) ? read : null;

    private static bool Shaped(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 2 && root[0].ValueKind == JsonValueKind.String && root[1].ValueKind == JsonValueKind.Array
                && root[1].EnumerateArray().All(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].ValueKind == JsonValueKind.String && p[1].ValueKind == JsonValueKind.String);
        }
        catch (JsonException) { return false; }
    }

    /// For the woken tab's documents, ahead of their scripts, until its first page has loaded: the top frame only, only
    /// for the same origin, and only into a session that's still empty. `kept` is KeepSession's JSON, put in as a
    /// literal; a script that doesn't parse throws before it writes anything.
    public static string RestoreSession(string kept) => $$"""
        (() => {
          try {
            if (window !== top) return;
            const [origin, items] = {{kept}};
            if (location.origin !== origin || sessionStorage.length) return;
            for (const [k, v] of items) sessionStorage.setItem(k, v);
          } catch (e) {}
        })();
        """;

    /// Away this long, a tab sleeps.
    public static TimeSpan Wait(SleepPace pace) => pace switch { SleepPace.Later => TimeSpan.FromHours(2), SleepPace.Sooner => TimeSpan.FromMinutes(10), _ => TimeSpan.FromMinutes(30) };

    public static string Title(SleepPace pace) => pace.ToString();

    /// What the line in Settings says under the choice.
    public static string Detail(SleepPace pace) => pace switch { SleepPace.Later => "After two hours away.", SleepPace.Sooner => "After ten minutes away.", _ => "After half an hour away." };

    /// As the Mac writes it: "later", "normal", "sooner".
    public static string Raw(SleepPace pace) => pace.ToString().ToLowerInvariant();

    /// The one chosen, Normal until someone chooses.
    public static SleepPace Pace(string? raw) => raw switch { "later" => SleepPace.Later, "sooner" => SleepPace.Sooner, _ => SleepPace.Normal };

    /// How long a tab has to go without being looked at: the pace's wait, no longer than Sooner's while saving power,
    /// or `seconds` when set (sleep.after, for the bench and the measurements).
    public static TimeSpan After(SleepPace pace, bool savingPower = false, double seconds = 0)
    {
        if (seconds > 0) return TimeSpan.FromSeconds(seconds);
        var wait = Wait(pace);
        return savingPower && wait > Wait(SleepPace.Sooner) ? Wait(SleepPace.Sooner) : wait;
    }

    /// How often the window looks (the same look closes the tabs left alone longest): a quarter of the wait, between
    /// five seconds and a minute.
    public static TimeSpan Every(TimeSpan after) => TimeSpan.FromSeconds(Math.Min(60, Math.Max(5, after.TotalSeconds / 4)));

    /// The wait when memory is short: five minutes on a warning, nothing when it is critical.
    public static TimeSpan Pressed(bool critical) => critical ? TimeSpan.Zero : TimeSpan.FromMinutes(5);

    /// The site a tab is kept awake for: a web page's host, lowercased.
    public static string? AwakeHost(Uri? address) => address is { IsAbsoluteUri: true } && address.Scheme is "http" or "https" && address.Host.Length > 0 ? address.Host.ToLowerInvariant() : null;

    /// Whether a tab's site is kept awake (Keep this site awake, on its card): the switch on and the host among `sites`
    /// (for a private tab, its own sites, which nothing keeps).
    public static bool KeepsAwake(Uri? address, bool keepsSitesAwake, IReadOnlySet<string> sites) => keepsSitesAwake && AwakeHost(address) is { } host && sites.Contains(host);

    /// The sites kept awake with this one's put in or taken out.
    public static IReadOnlySet<string> KeepAwake(IReadOnlySet<string> sites, Uri? address, bool on)
    {
        if (AwakeHost(address) is not { } host) return sites;
        var set = sites.ToHashSet();
        if (on) set.Add(host); else set.Remove(host);
        return set;
    }

    /// What the page is in the middle of that letting it go would cut off, nil when nothing: the part of staying awake
    /// that closing a tab asks too.
    public static string? Busy(IdleTab tab) =>
        tab.KeptAwake ? "kept awake for this site"
        : tab.Noisy ? "playing sound"
        : tab.VideoOut ? "its video is out"
        : tab.OnCall ? "on a call"
        : tab.RecordingScreen ? "recording the screen"
        : tab.Downloading ? "downloading"
        : tab.Asking ? "a question waiting"
        : tab.Opened ? "the page on screen came from it"
        : null;

    /// Why a tab has to stay awake, null when nothing keeps it. The clock is the caller's business. `shut`: the last
    /// window closed with the app running, so nothing is on screen and every pin is as one in a parked row.
    public static string? Awake(IdleTab tab, bool shut = false)
    {
        if (!shut && tab.OnScreen) return "on screen";
        if (tab.Pinned && !shut && !tab.Parked) return "pinned";
        if (tab.Pinned && tab.MayNotify) return "pinned, and its site may send notifications";
        if (tab.Bench) return "a bench tab";
        if (tab.Blank) return "blank";
        if (tab.Asleep) return "already asleep";
        if (!tab.HasPage) return "no page";
        if (tab.Loading) return "still loading";
        return Busy(tab);
    }

    /// Whether it has gone `wait` without being looked at.
    public static bool StillIdle(IdleTab tab, DateTimeOffset now, TimeSpan wait) => now - tab.Touched >= wait;

    /// Every tab (this space's row and the parked ones) that has gone long enough without being looked at and that
    /// nothing keeps awake, the one left longest first. The window sleeps them one after another, asking each page
    /// whether it holds anything typed and looking again (StillIdle, Awake) before each.
    public static IReadOnlyList<Guid> Due(IEnumerable<IdleTab> tabs, DateTimeOffset now, TimeSpan wait, bool shut = false) =>
        tabs.Where(t => StillIdle(t, now, wait) && Awake(t, shut) is null).OrderBy(t => t.Touched).Select(t => t.Id).ToList();

    /// The last window closed with the app running: every tab the rules let go sleeps now, whatever its time.
    /// `LookAgain`: one was still loading, to be looked at once more a little later (fifteen seconds on the Mac).
    public static (IReadOnlyList<Guid> Sleep, bool LookAgain) Shut(IEnumerable<IdleTab> tabs)
    {
        var all = tabs.ToList();
        return (all.Where(t => Awake(t, shut: true) is null).Select(t => t.Id).ToList(), all.Any(t => Awake(t, shut: true) == "still loading"));
    }
}
