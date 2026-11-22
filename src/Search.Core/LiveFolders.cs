namespace Search.Core;

// Live folders kept fresh (LiveFolders.swift's LiveFolders): fetched when made (the fetch that makes one), a little
// after launch, every half hour while Search runs, and on Refresh in the folder's menu; with the switch off
// (Settings › Spaces & Pins › Folders in pinned rows, with Pinned rows) nothing is fetched. A fetch that fails keeps
// the posts the folder had and says so in its heading's tooltip, and nothing else; which ones failed is kept here
// for as long as Search runs, never in pin-folders.json. A folder being fetched isn't fetched again until that ends.
//
// LiveSchedule is the clock's part, pure: when the next all-folders fetch is due. LiveFolders does the fetching and
// puts the posts in PinFolders; call it from the window's thread, as PinFolders is.

/// When every live folder is fetched: 5 seconds after launch, then every half hour from launch, as the Mac's timer.
/// A tick missed (the machine asleep) is one fetch when it wakes, not one per half hour missed.
public sealed class LiveSchedule
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(5), Every = TimeSpan.FromMinutes(30);

    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _started;

    public LiveSchedule(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _started = _now();
        Next = _started + First;
    }

    /// When the next fetch of every folder is due.
    public DateTimeOffset Next { get; private set; }
    /// How long until then, for a one-shot timer; zero when it is due.
    public TimeSpan Wait => Next - _now() is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    /// Whether every live folder is to be fetched now: true once per due time, and never while `on` is false (the
    /// time passes all the same, as the Mac's timer fires and finds the switch off).
    public bool Due(bool on)
    {
        var now = _now();
        if (now < Next) return false;
        Next = _started + Every * (Math.Floor((now - _started) / Every) + 1);
        return on;
    }
}

/// The live folders of every space, fetched and kept.
public sealed class LiveFolders(PinFolders store, LiveFetch fetch)
{
    private readonly HashSet<Guid> _failed = [], _fetching = [];
    /// Fetches end on whatever thread they end on when no window's is there to come back to (a test's).
    private readonly Lock _lock = new();

    /// Folders whose last fetch failed: their tooltip says so.
    public IReadOnlySet<Guid> Failed { get { lock (_lock) return _failed.ToHashSet(); } }
    public bool IsFetching(Guid id) { lock (_lock) return _fetching.Contains(id); }

    /// A live folder's heading's tooltip: its feed's site, or CouldNotRefresh.
    public string Tip(PinFolder folder) { lock (_lock) return LiveFeed.Tip(folder, _failed.Contains(folder.Id)); }

    /// New Live Folder…: the address asked for, read (a page's feed found); LiveFeed.Made makes the folder from it.
    public Task<LiveFeed.Fetched> Load(string url, CancellationToken cancel = default) => fetch.Load(url, cancel);

    /// Every live folder of every space, while the switch is on (a due LiveSchedule tick).
    public Task RefreshAll(bool on)
    {
        if (!on) return Task.CompletedTask;
        var fetches = new List<Task>();
        foreach (var (space, folders) in store.BySpace.ToList())
            foreach (var folder in folders.Where(f => f.IsLive)) fetches.Add(Refresh(folder.Id, space));
        return Task.WhenAll(fetches);
    }

    /// Refresh: one folder fetched, its posts replaced (ids kept for those seen before) and every window told, even
    /// when it failed and nothing changed, so its tooltip can say so. Nothing while it is already being fetched.
    public async Task Refresh(Guid id, Guid space)
    {
        string? feed;
        lock (_lock) if ((feed = store.Folders(space).FirstOrDefault(f => f.Id == id)?.Feed) is null || !_fetching.Add(id)) return;
        LiveFeed.Fetched result;
        try { result = await fetch.Load(feed); }
        finally { lock (_lock) _fetching.Remove(id); }
        lock (_lock)
        {
            if (LiveFeed.Refreshed(store.Folders(space), id, result) is not { } done) return;
            if (done.Error is null) _failed.Remove(id); else _failed.Add(id);
            store.Set(space, done.Folders, null, always: true);
        }
    }

    /// A folder deleted: nothing more to say about it.
    public void Forget(Guid id) { lock (_lock) _failed.Remove(id); }
}
