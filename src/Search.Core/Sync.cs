using System.Text.Json;

namespace Search.Core;

// Sync with your other computers, Macs included, through a folder you choose (Sync.swift, the part that isn't the
// window): OneDrive, Dropbox, iCloud Drive for Windows, a share. Search writes small files into it and whatever moves
// that folder between machines does the rest. No account, no server, nothing of ours in between. Off unless turned on;
// the folder is kept as a plain path.
//
// In the folder, "Search Sync/<scope>/<device>.json": one file per machine per scope, written only by that machine,
// so two never write the same file. The scopes: "app" for the settings below, and "profile-<ID>" for each profile's
// spaces, pins and bookmarks; a profile's never mixes with another's. Each file is the whole merged view as that
// machine last had it, item by item with stamps and tombstones (SyncMerge.cs has the rules and the format). What this
// machine last agreed, per scope, is kept beside its other files (sync-<scope>.json), never in the shared folder.
//
// What syncs: spaces (name, colour or gradient, strength, icon, order, and whether a space has sign-ins of its own;
// never the sign-ins themselves, its cookies, tabs or downloads folder), pins (each space's and the ones every space
// shows: letter, name, page, order), bookmarks, and the settings in SyncSettings. What never does: history,
// passwords, cookies and site data, open tabs that aren't pinned, tab groups (they hold open tabs), private windows,
// extensions, the downloads folder, window sizes.
//
// Profiles: Personal is the same profile everywhere. Another syncs with the profile of the same id elsewhere; one
// made separately on each machine has a different id and stays apart.
//
// The app runs a pass (RunAsync) a moment after anything synced changes here, when a file in the folder changes,
// every half minute and when Search comes forward, and reacts to what it says changed. A pass reads a handful of
// small files and writes this machine's only when it says something new. Called from the UI thread: the models it
// reads and changes are the window's.

/// One setting as Sync carries it: read as its item's text (null when this machine can't say it in the Mac's
/// terms), and written from one (true when that changed it).
public sealed record SyncSetting(string Key, Func<string?> Read, Func<string, bool> Write);

/// The settings that follow you: how Search looks and behaves, not what belongs to this machine.
public static class SyncSettings
{
    /// Every key the Mac syncs. Those Windows has no place for yet are carried along untouched, never lost.
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "look", "glyph", "search.engine", "search.custom", "search.suggest", "search.keywords", "spaces",
        "pins.list", "pins.home", "tabs.close.after", "tabs.newPlace", "tabs.shortcuts", "shield",
        "privacy.clickIDs", "privacy.gpc", "bookmarks.bar", "quit.confirm",
    };

    /// The ones Windows keeps somewhere: only these are read here, written here, and deleted when gone.
    public static readonly IReadOnlySet<string> Mapped = new HashSet<string>(StringComparer.Ordinal) { "look", "search.engine", "search.custom", "spaces", "tabs.close.after", "shield" };

    /// Each mapped setting where Windows keeps it: the look and the engine and the ad blocker in Prefs, spaces on or
    /// off in Spaces' notes, how long tabs are kept in the notes (Closing.Key).
    public static IReadOnlyList<SyncSetting> For(Prefs prefs, Spaces spaces, IDictionary<string, string> notes) =>
    [
        new("look", () => SortedJson.String(prefs.Theme switch { Theme.Light => "light", Theme.Dark => "dark", _ => "system" }),
            text => SyncMerge.Text(text) switch { "light" => Put(prefs.Theme, Theme.Light, v => prefs.Theme = v), "dark" => Put(prefs.Theme, Theme.Dark, v => prefs.Theme = v), "system" => Put(prefs.Theme, Theme.System, v => prefs.Theme = v), _ => false }),
        new("search.engine", () => SearchEngines.FromId(prefs.Engine) is { } engine ? SortedJson.String(engine.Id()) : null,
            text => SyncMerge.Text(text) is { } id && SearchEngines.FromId(id) is { } engine && Put(prefs.Engine, engine.Id(), v => prefs.Engine = v)),
        new("search.custom", () => SortedJson.String(prefs.CustomEngine), text => SyncMerge.Text(text) is { } custom && Put(prefs.CustomEngine, custom, v => prefs.CustomEngine = v)),
        new("spaces", () => SortedJson.Bool(spaces.UsesSpaces), text => Flag(text) is { } on && Put(spaces.UsesSpaces, on, v => spaces.UsesSpaces = v)),
        new("tabs.close.after", () => SortedJson.Number((int)(notes.TryGetValue(Closing.Key, out var hours) && long.TryParse(hours, out var h) ? Closing.Life(h) : TabLife.Never)),
            text => Whole(text) is { } h && h is 0 or 12 or 24 or 168 or 720 && Put(notes.TryGetValue(Closing.Key, out var was) ? was : "0", h.ToString(System.Globalization.CultureInfo.InvariantCulture), v => notes[Closing.Key] = v)),
        new("shield", () => SortedJson.Bool(prefs.Shield), text => Flag(text) is { } on && Put(prefs.Shield, on, v => prefs.Shield = v)),
    ];

    /// The settings as items. One this machine can't say (an engine the Mac doesn't know) is what was agreed.
    public static Dictionary<string, string> Items(IEnumerable<SyncSetting> settings, IReadOnlyDictionary<string, string>? agreed = null)
    {
        var made = new Dictionary<string, string>();
        foreach (var setting in settings)
            if ((setting.Read() ?? (agreed is not null && agreed.TryGetValue("set:" + setting.Key, out var was) ? was : null)) is { } text) made["set:" + setting.Key] = text;
        return made;
    }

    /// The merged items made so. True when any setting changed.
    public static bool Apply(IReadOnlyDictionary<string, string> live, IEnumerable<SyncSetting> settings)
    {
        var changed = false;
        foreach (var setting in settings)
            if (live.TryGetValue("set:" + setting.Key, out var text) && setting.Write(text)) changed = true;
        return changed;
    }

    private static bool Put<T>(T now, T value, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(now, value)) return false;
        set(value);
        return true;
    }

    private static bool? Flag(string text) { try { using var d = JsonDocument.Parse(text); return SwiftJson.Bool(d.RootElement); } catch (JsonException) { return null; } }
    private static long? Whole(string text) { try { using var d = JsonDocument.Parse(text); return SwiftJson.Long(d.RootElement); } catch (JsonException) { return null; } }
}

/// Another machine whose file was read, for Settings to say who was last seen, and when it last wrote.
public sealed record SyncPeer(string Device, string Name, DateTimeOffset Written);

/// What a pass did. The models have already been changed and have said so (Spaces, Pins, Bookmarks, Prefs); this
/// is for what the window has to do besides: close the tabs of the spaces removed, redraw what changed.
public sealed record SyncResult
{
    /// False when no pass ran: sync off, a pass already running, the folder not there.
    public bool Ran { get; init; }
    /// Why it couldn't run, or couldn't write, for Settings.
    public string? Problem { get; init; }
    /// Some setting was changed to another machine's.
    public bool Settings { get; init; }
    /// The profiles whose spaces, pins or bookmarks were changed to another machine's.
    public IReadOnlySet<Guid> Profiles { get; init; } = new HashSet<Guid>();
    /// Spaces deleted on another machine and so here: their session files and pins are gone; the window closes their tabs.
    public IReadOnlyList<Guid> RemovedSpaces { get; init; } = [];
    /// The other machines whose files were read, by name.
    public IReadOnlyList<string> Others { get; init; } = [];

    public bool Changed => Settings || Profiles.Count > 0;
}

public sealed class Sync
{
    public const string FolderKey = "sync.folder", DeviceKey = "sync.device";
    /// The folder Search makes inside the one chosen.
    public const string Inside = "Search Sync";
    public const string AppScope = "app";
    /// A file bigger than this in the folder is not read.
    public const long MostBytes = 32L << 20;
    /// What a file without a name is called.
    public const string Unnamed = "Another computer";

    private readonly IDictionary<string, string> _notes;
    private readonly Spaces _spaces;
    private readonly Pins _pins;
    private readonly Profiles _profiles;
    private readonly Func<Guid, Bookmarks> _bookmarks;
    private readonly PinFolders? _folders;
    private readonly IReadOnlyList<SyncSetting> _settings;
    /// What was last agreed per scope, once read: the files lag behind the Disk queue, memory doesn't. Null in it is
    /// a scope with nothing agreed yet (a machine joining).
    private readonly Dictionary<string, Dictionary<string, SyncEntry>?> _agreed = [];

    /// `notes`: where the folder and this machine's id are kept (the Mac's settings). `bookmarks`: each profile's
    /// list, the profiles' own by default. `settings`: the settings that sync, SyncSettings.For by default.
    /// `name`: this machine's name in the folder, the PC's by default.
    public Sync(IDictionary<string, string> notes, Prefs prefs, Spaces spaces, Pins pins, Profiles profiles, Func<Guid, Bookmarks>? bookmarks = null, PinFolders? folders = null,
        IReadOnlyList<SyncSetting>? settings = null, string? name = null)
    {
        _notes = notes;
        _spaces = spaces;
        _pins = pins;
        _profiles = profiles;
        _bookmarks = bookmarks ?? (id => profiles.Bookmarks(id));
        _folders = folders;
        _settings = settings ?? SyncSettings.For(prefs, spaces, notes);
        Name = name ?? MachineName();
        // Made once and kept with the settings, so each test world is a machine of its own.
        Device = notes.TryGetValue(DeviceKey, out var kept) && kept.Length > 0 ? kept : (notes[DeviceKey] = SwiftJson.Uuid(Guid.NewGuid()));
        Folder = notes.TryGetValue(FolderKey, out var path) && path.Length > 0 ? path : null;
    }

    /// This machine's id in the folder.
    public string Device { get; }
    /// This machine's name in the folder: the PC's name.
    public string Name { get; }
    /// The folder chosen; null while sync is off.
    public string? Folder { get; private set; }
    public bool On => Folder is not null;
    /// The folder Search writes in, inside the one chosen: watch it, with its subfolders, for other machines' files.
    public string? Root => Folder is null ? null : Path.Combine(Folder, Inside);
    public DateTimeOffset? LastSynced { get; private set; }
    public string? Problem { get; private set; }
    /// The other machines whose files the last pass read, by name.
    public IReadOnlyList<string> Others { get; private set; } = [];
    public IReadOnlyList<SyncPeer> Peers { get; private set; } = [];
    /// A pass is under way: changes the models report meanwhile are its own, not a reason for another.
    public bool Running { get; private set; }

    public IReadOnlyList<string> Scopes => [AppScope, .. _profiles.List.Select(p => Scope(p.Id))];
    public static string Scope(Guid profile) => "profile-" + SwiftJson.Uuid(profile);
    public static string StateFile(string scope) => Store.File($"sync-{scope}.json");

    /// The PC's name, as Settings › System › About shows it.
    public static string MachineName()
    {
        try { return System.Net.Dns.GetHostName() is { Length: > 0 } host ? host : Environment.MachineName; }
        catch (System.Net.Sockets.SocketException) { return Environment.MachineName; }
    }

    // On and off.

    /// The switch turned on with a folder; the app runs the first pass. Always as a machine joining: what was agreed
    /// with another folder, or with this one long ago, says nothing about what is there now.
    public void TurnOn(string folder)
    {
        Forget();
        _notes[FolderKey] = folder;
        Folder = folder;
        Problem = null;
    }

    /// Off: this machine's files leave the folder and what it had agreed is forgotten, so turning it on again starts
    /// as a machine joining. What is in the browser stays as it is.
    public void TurnOff()
    {
        if (Root is { } root) foreach (var scope in Scopes) Delete(Path.Combine(root, scope, Device + ".json"));
        Forget();
        _notes.Remove(FolderKey);
        Folder = null;
        LastSynced = null;
        Problem = null;
        Others = [];
        Peers = [];
    }

    /// Nothing agreed in any scope, in memory at once and on disk through the queue.
    private void Forget()
    {
        _agreed.Clear();
        foreach (var scope in Scopes) { Delete(StateFile(scope)); _agreed[scope] = null; }
    }

    /// Through the queue, so a write still on its way can't bring the file back after it.
    private static void Delete(string file) => Disk.Write(file, () => { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; });

    // A pass.

    /// One pass, all of it here.
    public SyncResult Run(DateTimeOffset? now = null)
    {
        if (Begin() is { } early) return early;
        try { return Settle(Plan().Select(Gather).ToList(), now ?? DateTimeOffset.UtcNow); }
        finally { Running = false; }
    }

    /// One pass, the folder read off the UI thread (a file a cloud drive hasn't brought down yet is fetched as it is
    /// read), then merged and applied back where it was called.
    public async Task<SyncResult> RunAsync(DateTimeOffset? now = null)
    {
        if (Begin() is { } early) return early;
        try
        {
            var jobs = Plan();
            var gathered = await Task.Run(() => jobs.Select(Gather).ToList());
            return Settle(gathered, now ?? DateTimeOffset.UtcNow);
        }
        finally { Running = false; }
    }

    /// Null to go ahead. A folder not there (a disk not plugged in, the cloud drive not running) is no reason to
    /// think everything was deleted: nothing happens.
    private SyncResult? Begin()
    {
        if (Folder is null || Running) return new SyncResult { Problem = Problem };
        if (!Directory.Exists(Folder)) { Problem = "The folder isn’t there"; return new SyncResult { Problem = Problem }; }
        Running = true;
        return null;
    }

    private sealed record Job(string Scope, string Folder, string Own, bool Known, Dictionary<string, SyncEntry>? Agreed);
    private sealed record Gathered(Job Job, bool Writable, Dictionary<string, SyncEntry>? Agreed, List<SyncFile> Theirs, SyncFile? Own);

    private List<Job> Plan() => Scopes.Select(scope =>
    {
        var folder = Path.Combine(Root!, scope);
        var known = _agreed.TryGetValue(scope, out var agreed);
        return new Job(scope, folder, Path.Combine(folder, Device + ".json"), known, agreed);
    }).ToList();

    /// Files only, nothing of the models: safe off the UI thread.
    private Gathered Gather(Job job)
    {
        try { Directory.CreateDirectory(job.Folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return new(job, false, null, [], null); }
        var agreed = job.Known ? job.Agreed : Read(StateFile(job.Scope)) is { } state ? SyncMerge.DecodeState(state) : null;
        var theirs = new List<SyncFile>();
        string[] files;
        try { files = Directory.GetFiles(job.Folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            // iCloud Drive keeps a file it has let go of as ".<name>.icloud": there for a later pass.
            if (name.StartsWith('.') && name.EndsWith(".json.icloud", StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".json", StringComparison.Ordinal) || string.Equals(file, job.Own, StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(file, MostBytes) is { } data && SyncMerge.Decode(data, job.Scope) is { } read && read.Device != Device) theirs.Add(read);
        }
        var own = Read(job.Own) is { } mine ? SyncMerge.Decode(mine, job.Scope) : null;
        return new(job, true, agreed, theirs, own);
    }

    private static byte[]? Read(string file, long most = long.MaxValue)
    {
        try { return new FileInfo(file).Length < most ? File.ReadAllBytes(file) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private SyncResult Settle(List<Gathered> gathered, DateTimeOffset now)
    {
        var seconds = (now - DateTimeOffset.UnixEpoch).TotalSeconds;
        string? problem = null;
        var settings = false;
        var profiles = new HashSet<Guid>();
        var removed = new List<Guid>();
        var peers = new Dictionary<string, SyncPeer>();
        foreach (var g in gathered)
        {
            if (!g.Writable) { problem = "Search can’t write in that folder"; continue; }
            foreach (var file in g.Theirs)
            {
                var written = DateTimeOffset.UnixEpoch.AddSeconds(Math.Clamp(file.Written, 0, 253_402_300_799));
                if (!peers.TryGetValue(file.Device, out var seen) || seen.Written < written) peers[file.Device] = new(file.Device, file.Name ?? Unnamed, written);
            }
            if (g.Job.Scope == AppScope)
                RunScope(g, seconds, agreed => SyncSettings.Items(_settings, agreed), live => settings |= SyncSettings.Apply(live, _settings));
            else if (_profiles.List.FirstOrDefault(p => Scope(p.Id) == g.Job.Scope) is { } profile)
                RunScope(g, seconds, agreed => Items(profile.Id, agreed), live => { if (Apply(live, profile, removed)) profiles.Add(profile.Id); });
        }
        Problem = problem;
        if (problem is null) LastSynced = now;
        Others = gathered.SelectMany(g => g.Theirs).Select(f => f.Name ?? Unnamed).Distinct().Order(SortedJson.SwiftOrder).ToList();
        Peers = peers.Values.OrderBy(p => p.Name, SortedJson.SwiftOrder).ToList();
        return new SyncResult { Ran = true, Problem = problem, Settings = settings, Profiles = profiles, RemovedSpaces = removed, Others = Others };
    }

    /// One scope: stamped, merged with the other machines' files, applied, and written.
    private void RunScope(Gathered g, double now, Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>> current, Action<Dictionary<string, string>> apply)
    {
        var scope = g.Job.Scope;
        var @base = g.Agreed ?? new Dictionary<string, SyncEntry>();
        var agreed = SyncMerge.Live(@base);
        var here = Keep(current(agreed), agreed);
        var merged = SyncMerge.Pass(here, g.Agreed, g.Theirs.Select(f => f.Items).ToList(), Device, now, SyncItems.Understood);
        var live = SyncMerge.Live(merged);
        if (!SyncMerge.Same(live, here)) apply(live);
        if (SyncMerge.Same(merged, @base)) _agreed[scope] = g.Agreed;
        else
        {
            _agreed[scope] = merged;
            Disk.Write(StateFile(scope), () => SyncMerge.EncodeState(merged));
        }
        // This machine's file, only when it says something new.
        if (g.Own is { } own && SyncMerge.Same(own.Items, merged) && own.Name == Name) return;
        var file = new SyncFile(SyncMerge.Format, Device, Name, scope, now, merged);
        Disk.Write(g.Job.Own, () => SyncMerge.Encode(file));
    }

    /// The text agreed for an item where this machine's says the same thing another way (one machine writing 135.0
    /// where another writes 135): a value that hasn't changed must never look as if it had, or the two would take
    /// turns stamping it.
    private static Dictionary<string, string> Keep(Dictionary<string, string> current, IReadOnlyDictionary<string, string> agreed)
    {
        foreach (var (key, text) in current.ToList())
            if (agreed.TryGetValue(key, out var was) && was != text && SortedJson.Same(was, text)) current[key] = was;
        return current;
    }

    // A profile's things.

    /// A profile's spaces, pins and bookmarks as items.
    private Dictionary<string, string> Items(Guid profile, IReadOnlyDictionary<string, string> agreed)
    {
        var spaces = _spaces.Of(profile);
        var bySpace = spaces.Where(s => _pins.Defs(s.Id).Count > 0).ToDictionary(s => s.Id, s => _pins.Defs(s.Id));
        var made = SyncItems.Spaces(spaces, agreed);
        foreach (var (key, text) in SyncItems.Pins(bySpace, _pins.Shared(profile), agreed)) made.TryAdd(key, text);
        foreach (var (key, text) in SyncItems.Bookmarks(_bookmarks(profile).Roots)) made.TryAdd(key, text);
        return made;
    }

    /// The merged items made so: spaces first, so pins have their space. True when anything changed.
    private bool Apply(IReadOnlyDictionary<string, string> live, Profile profile, List<Guid> removed)
    {
        var id = profile.Id;
        var local = _spaces.Of(id);
        var spaces = SyncItems.ApplySpaces(live, local, id, profile.Home);
        var changed = false;
        // A space deleted elsewhere: as Delete Space does here, its tabs, its pins, and for one with sign-ins of its
        // own, those too. The window closes its tabs (RemovedSpaces).
        foreach (var gone in local.Where(l => spaces.All(s => s.Id != l.Id)).ToList())
        {
            if (!_spaces.Remove(gone.Id)) continue;
            _pins.Forget(gone.Id, _folders);
            removed.Add(gone.Id);
            changed = true;
        }
        if (!spaces.SequenceEqual(_spaces.Of(id)))
        {
            // The order is the one merged, wherever the home is in it.
            _spaces.KeepOrder();
            _spaces.Write(spaces, id);
            changed = true;
        }

        var ids = spaces.Select(s => s.Id).ToList();
        var localPins = ids.ToDictionary(s => s, s => _pins.Defs(s));
        var (bySpace, shared) = SyncItems.ApplyPins(live, ids, localPins, _pins.Shared(id));
        if (ids.Any(s => !_pins.Defs(s).SequenceEqual(bySpace.GetValueOrDefault(s) ?? [])) || !_pins.Shared(id).SequenceEqual(shared))
        {
            _pins.Adopt(bySpace, ids, shared, id);
            changed = true;
        }

        var tree = SyncItems.ApplyBookmarks(live);
        var bookmarks = _bookmarks(id);
        if (!Bookmarks.Same(tree, bookmarks.Roots))
        {
            bookmarks.Adopt(tree);
            changed = true;
        }
        return changed;
    }

    // For Settings.

    /// Where, in words: the OneDrive folder by that name.
    public string Place => Folder is null ? "" : Friendly(Folder);

    /// Where, and when last, in a line: "OneDrive\Search · MacBook Air · a minute ago". `ago` says how long ago.
    public string Summary(Func<DateTimeOffset, string> ago)
    {
        if (Folder is null) return "";
        if (Problem is { } problem) return $"{Place}: {problem.ToLowerInvariant()}";
        if (LastSynced is not { } last) return Place;
        return $"{Place} · {(Others.Count == 0 ? "no other computer yet" : string.Join(", ", Others))} · {ago(last)}";
    }

    private static string Friendly(string path)
    {
        foreach (var name in new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } drive && path.StartsWith(drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "OneDrive" + path[drive.TrimEnd('\\').Length..];
        return path;
    }
}
