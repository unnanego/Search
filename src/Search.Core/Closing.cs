namespace Search.Core;

// Tabs that close themselves, as in Arc (Closing.swift).
//
// Everything you open is for now, unless you say it is for keeping. A tab you haven't looked at for twelve hours (or a
// day, a week, a month: Settings › Tabs) closes the way Ctrl+W would close it: Ctrl+Shift+T brings it back while it
// is recent, History › Archived Tabs for a month (Archive.cs), and History has its page for good. What stays is what
// you said to keep: a pinned tab, a tab you named, a tab in a group, or a site kept awake from its card.
//
// The time counts from when you last left the tab, or let go of it (unpinned, ungrouped, unnamed: LetGo), and goes on
// counting while Search is quit: the session file carries both (Session.Entry.Touched, .LetGo). Without that,
// yesterday's tabs would come back each morning as if just looked at.
//
// Nothing is closed from under you. The page on screen stays (in this window or another), and so does one busy the way
// a tab kept awake is (Sleep.Busy); a private tab too, which nothing could bring back; a bench tab; and a blank one,
// which costs nothing (closing the last would close the window). A split goes whole or not at all. The window asks each
// page whether it holds something typed, one after another, closes the lot only when none does, and looks again
// (IsDue) first: you may have gone back to one while they answered. Never, unless chosen.

/// How long a tab is left before it closes, by its hours, as the Mac keeps it ("tabs.close.after").
public enum TabLife { Never = 0, HalfDay = 12, Day = 24, Week = 168, Month = 720 }

public static class Closing
{
    public const string Key = "tabs.close.after";

    /// Null when tabs never close.
    public static TimeSpan? Interval(TabLife life) => life == TabLife.Never ? null : TimeSpan.FromHours((int)life);

    public static string Title(TabLife life) => life switch
    {
        TabLife.HalfDay => "After 12 hours", TabLife.Day => "After a day", TabLife.Week => "After a week", TabLife.Month => "After a month", _ => "Never",
    };

    /// The one chosen, from the number of hours the Mac writes; Never for anything else.
    public static TabLife Life(long hours) => hours is 12 or 24 or 168 or 720 ? (TabLife)(int)hours : TabLife.Never;

    /// How long a tab is left before it closes, null when tabs never do. `seconds` stands in for the chosen time when
    /// set (close.after, for the tests), only while tabs close at all.
    public static TimeSpan? After(TabLife life, double seconds = 0) => Interval(life) is { } chosen ? seconds > 0 ? TimeSpan.FromSeconds(seconds) : chosen : null;

    /// Since when a tab counts as left: the later of the last look and the last letting go.
    public static DateTimeOffset LeftSince(DateTimeOffset touched, DateTimeOffset? letGo) => letGo is { } l && l > touched ? l : touched;

    /// Why a tab stays open however long it is left, null when nothing keeps it. The clock is the caller's business.
    public static string? Stays(IdleTab tab) =>
        tab.OnScreen ? "on screen"
        : tab.OnScreenElsewhere ? "on screen in another window"
        : tab.Pinned ? "pinned"
        : tab.Named ? "named"
        : tab.Grouped ? "in a group"
        : tab.Private ? "private"
        : tab.Bench ? "a bench tab"
        : tab.Blank ? "blank"
        : Sleep.Busy(tab);

    /// Whether a tab is past its time and nothing keeps it: what the window asks again just before it closes.
    public static bool IsDue(IdleTab tab, DateTimeOffset now, TimeSpan wait) => LeftSince(tab.Touched, tab.LetGo) <= now - wait && Stays(tab) is null;

    /// Every tab left alone past its time, in this space's row and the others' (`tabs`), each list closing together:
    /// a split is one, both or all of its pages due or none goes (`splits`: every row's). Empty when tabs never close.
    public static IReadOnlyList<IReadOnlyList<Guid>> Due(IReadOnlyList<IdleTab> tabs, IReadOnlyList<TabSplit> splits, DateTimeOffset now, TabLife life, double seconds = 0)
    {
        if (After(life, seconds) is not { } wait) return [];
        var due = tabs.Where(t => IsDue(t, now, wait)).ToList();
        var going = due.Select(t => t.Id).ToHashSet();
        var taken = new HashSet<Guid>();
        var made = new List<IReadOnlyList<Guid>>();
        foreach (var tab in due)
        {
            if (taken.Contains(tab.Id)) continue;
            var pair = splits.FirstOrDefault(s => s.Contains(tab.Id));
            if (pair is not null && !pair.Tabs.All(going.Contains)) continue;
            var together = pair is null ? [tab.Id] : due.Where(t => pair.Contains(t.Id)).Select(t => t.Id).ToList();
            taken.UnionWith(together);
            made.Add(together);
        }
        return made;
    }

    /// A tab's last look brought back from the session: never later than now (a clock set wrong once would keep the
    /// tab forever), and now for a session written before tabs kept it.
    public static DateTimeOffset Touched(Session.Entry entry, DateTimeOffset now) => entry.Touched is { } t && t < now ? t : now;

    /// Its last letting go, the same way; null when it never was.
    public static DateTimeOffset? LetGo(Session.Entry entry, DateTimeOffset now) => entry.LetGo is { } l ? l < now ? l : now : null;

    /// What the session writes as its last look: a page on screen is being looked at now, so quitting on it is leaving it.
    public static DateTimeOffset ToWrite(DateTimeOffset touched, bool onScreen, DateTimeOffset now) => onScreen ? now : touched;
}
