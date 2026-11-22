using System.Globalization;
using System.Text.Json;

namespace Search.Core;

// Pinned tabs are the same in every window (Pins.swift): pinned, unpinned, moved, relettered or renamed in one, so
// they are in the others. Each window holds its own tab for each pin, its own page wherever you took it, and only
// what makes the pin a pin is shared: its letter, its name, the page it was pinned at, its place among the pins.
// Per space, as the pins always were.
//
// pins.json holds them, in the Mac's shape key for key: {"<SPACE-UUID>": [PinDef], "shared": [PinDef],
// "shared:<PROFILE-UUID>": [PinDef]}. The oldest window's session file keeps its pins as it always did (pin, pinID,
// home, listed on its entries), so an older version finds them there; with no pins.json yet they are taken from
// there. Beside the spaces' pins, the ones every space shows first while Pins in every space is on (Arc's
// favourites): Personal's under "shared", another profile's under "shared:<id>".
//
// The row rules (pin, unpin, settle, reletter, go home, reconcile) are pure functions over PinnedTab, the facts a
// window's tab hands in; the window applies what comes back to its tabs.

/// What makes a pin a pin, shared by every window.
public sealed record PinDef(Guid Id, string Letter, string Home, string Title)
{
    public string? Name { get; init; }
    /// A row, not a square. Null rather than false, so a pins file with no rows in it is written as it always was.
    public bool? Listed { get; init; }
    /// The folder among the pinned rows that holds it (PinFolders.cs), null at the top of them. Written only when it is in one.
    public Guid? Folder { get; init; }
    /// The Split View it was pinned in, shared with the other pins of that split (the Mac's "pin a split as a split").
    /// This build doesn't draw pinned splits yet; the field is kept so a file the Mac wrote keeps it.
    public Guid? Pair { get; init; }
}

/// What the pin rules need of a window's tab, and what they hand back for it.
public sealed record PinnedTab(Guid Id)
{
    /// Its letter, when it is a pin.
    public string? Pin { get; init; }
    /// Which of the pins it is, in every window.
    public Guid? PinId { get; init; }
    /// Kept as a row under the squares.
    public bool Listed { get; init; }
    /// One every space shows (set when the row is built: pins.json, not the session, says which).
    public bool Everywhere { get; init; }
    /// For a pinned row, the folder that holds it.
    public Guid? Folder { get; init; }
    /// The split it was pinned in (PinDef.Pair), carried through as it came.
    public Guid? Pair { get; init; }
    /// For a pin, the page it was pinned at.
    public string? Home { get; init; }
    /// Where the tab is now, asleep or not (Tab.Url's AbsoluteUri).
    public string? Url { get; init; }
    public string Title { get; init; } = "";
    public string? Name { get; init; }
    /// A private tab is never a pin: its page would outlive it in every other window.
    public bool Private { get; init; }
    /// Handed back by Reconcile for a pin new to this window: make a tab asleep at Home for it, titled Title.
    public bool Fresh { get; init; }

    public bool IsPin => Pin is not null;
}

/// A space's pins changed (Space set), or a profile's shared ones (Profile set). `From` is the window that changed
/// them, so it can pass over its own news.
public sealed record PinsChange(object? From, Guid? Space, Guid? Profile);

public sealed class Pins
{
    /// Where pins.json keeps the shared pins, beside the spaces' ids.
    public const string SharedKey = "shared";
    public static readonly Guid PersonalProfile = new("00000000-0000-0000-0000-0000000000A1");

    private readonly string _fileName;
    private readonly Dictionary<Guid, IReadOnlyList<PinDef>> _bySpace = [];
    private readonly Dictionary<Guid, IReadOnlyList<PinDef>> _sharedBy = [];

    /// A space's pins changed in one window; the others follow.
    public event Action<PinsChange>? Changed;

    /// `spaces` are the ones whose session files hold the pins before there is a pins.json (every space's).
    public Pins(string file = "pins.json", IEnumerable<Guid>? spaces = null) { _fileName = file; Load(spaces ?? [Session.FirstSpace]); }

    private string FilePath => Store.File(_fileName);

    /// Every space that has pins, and its pins in their order.
    public IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>> BySpace => _bySpace;
    public IReadOnlyList<PinDef> Defs(Guid space) => _bySpace.TryGetValue(space, out var defs) ? defs : [];
    /// The pins every space of a profile shows, in their order.
    public IReadOnlyList<PinDef> Shared(Guid profile) => _sharedBy.TryGetValue(profile, out var defs) ? defs : [];

    /// Read once. Before there is a pins.json, the pins are the oldest window's, from its session files. One that
    /// won't decode is set aside first: the Mac took the sessions' pins over it; here it is kept too.
    private void Load(IEnumerable<Guid> spaces)
    {
        byte[] data;
        try { data = File.ReadAllBytes(FilePath); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { Migrate(spaces); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (ReadAll(data) is { } saved)
        {
            foreach (var (key, defs) in saved)
                if (SwiftJson.Uuid(key) is { } space) _bySpace[space] = defs;
                else if (Profile(key) is { } profile && defs.Count > 0) _sharedBy[profile] = defs;
            return;
        }
        Store.Quarantine(FilePath);
        Migrate(spaces);
    }

    private void Migrate(IEnumerable<Guid> spaces)
    {
        foreach (var space in spaces) if (FromSession(Session.Read(space)) is { Count: > 0 } defs) _bySpace[space] = defs;
        Save();
    }

    /// A window's pins for a space, as they now are: kept, written, and passed to the other windows when they differ.
    public void Set(Guid space, IReadOnlyList<PinDef> defs, object? from = null)
    {
        if (Defs(space).SequenceEqual(defs)) return;
        if (defs.Count == 0) _bySpace.Remove(space); else _bySpace[space] = defs.ToList();
        Save();
        Changed?.Invoke(new(from, space, null));
    }

    /// The shared pins as they now are, passed to the other windows, which take them into every space they hold.
    /// True when they changed.
    public bool SetShared(Guid profile, IReadOnlyList<PinDef> defs, object? from = null)
    {
        if (Shared(profile).SequenceEqual(defs)) return false;
        if (defs.Count == 0) _sharedBy.Remove(profile); else _sharedBy[profile] = defs.ToList();
        Save();
        Changed?.Invoke(new(from, null, profile));
        return true;
    }

    /// Unpin on one every space shows: out of them all, as unpinning in one window is out of every window.
    public bool Unshare(Guid profile, Guid pinId, object? from = null) => SetShared(profile, Shared(profile).Where(d => d.Id != pinId).ToList(), from);

    /// Pins as another machine left them (Sync): each of `spaces` given its list and the profile its shared one, and
    /// every window told of what changed.
    public void Adopt(IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>> defs, IEnumerable<Guid> spaces, IReadOnlyList<PinDef> shared, Guid profile)
    {
        var changed = new List<Guid>();
        foreach (var space in spaces)
        {
            var theirs = defs.TryGetValue(space, out var d) ? d : [];
            if (Defs(space).SequenceEqual(theirs)) continue;
            if (theirs.Count == 0) _bySpace.Remove(space); else _bySpace[space] = theirs.ToList();
            changed.Add(space);
        }
        var sharedChanged = !Shared(profile).SequenceEqual(shared);
        if (sharedChanged) { if (shared.Count == 0) _sharedBy.Remove(profile); else _sharedBy[profile] = shared.ToList(); }
        if (changed.Count == 0 && !sharedChanged) return;
        Save();
        foreach (var space in changed) Changed?.Invoke(new(null, space, null));
        if (sharedChanged) Changed?.Invoke(new(null, null, profile));
    }

    /// A profile deleted: the pins its spaces all showed go with it.
    public void ForgetShared(Guid profile) { if (_sharedBy.Remove(profile)) Save(); }

    /// A space deleted: its pins go with it, and its pin folders.
    public void Forget(Guid space, PinFolders? folders = null) { folders?.Forget(space); if (_bySpace.Remove(space)) Save(); }

    private void Save()
    {
        var bySpace = _bySpace.ToDictionary(p => p.Key, p => p.Value);
        var all = _sharedBy.ToDictionary(p => p.Key, p => p.Value);
        Disk.Write(FilePath, () => Encode(bySpace, all.TryGetValue(PersonalProfile, out var mine) ? mine : [], all.Where(p => p.Key != PersonalProfile).ToDictionary(p => p.Key, p => p.Value)));
    }

    // The two lists, worked out.

    /// A space's own pin, shown by every space from now on: at the end of the shared ones, and in no folder.
    public static (IReadOnlyList<PinDef> Own, IReadOnlyList<PinDef> Shared) Share(Guid id, IReadOnlyList<PinDef> own, IReadOnlyList<PinDef> shared) =>
        own.FirstOrDefault(d => d.Id == id) is { } pin ? (own.Where(d => d.Id != id).ToList(), [.. shared, pin with { Folder = null }]) : (own, shared);

    /// A shared pin kept by one space only: first among its own.
    public static (IReadOnlyList<PinDef> Own, IReadOnlyList<PinDef> Shared) Unshare(Guid id, IReadOnlyList<PinDef> own, IReadOnlyList<PinDef> shared) =>
        shared.FirstOrDefault(d => d.Id == id) is { } pin ? ([pin, .. own], shared.Where(d => d.Id != id).ToList()) : (own, shared);

    /// A row's pins, as the row has them, into the space's own and the shared ones, each in the row's order.
    public static (IReadOnlyList<PinDef> Own, IReadOnlyList<PinDef> Shared) Split(IReadOnlyList<PinDef> defs, IReadOnlySet<Guid> shared) =>
        (defs.Where(d => !shared.Contains(d.Id)).ToList(), defs.Where(d => shared.Contains(d.Id)).ToList());

    /// What a space's row holds: the shared pins, then its own, while the shared ones show; its own alone otherwise.
    public static IReadOnlyList<PinDef> Row(IReadOnlyList<PinDef> own, IReadOnlyList<PinDef> shared, bool showing)
    {
        if (!showing) return own;
        var ids = shared.Select(d => d.Id).ToHashSet();
        return [.. shared, .. own.Where(d => !ids.Contains(d.Id))];
    }

    /// What a row's shared pins say the shared list now is: nothing while they don't show, and nothing from a row
    /// that holds none (a row not yet given them, the switch just turned on, must not wipe them).
    public static IReadOnlyList<PinDef>? SharedToWrite(IReadOnlyList<PinDef> rowShared, bool showing) => showing && rowShared.Count > 0 ? rowShared : null;

    // The session's pins.

    /// The pins a session file holds, as an older version left them: the first launch with pins.json takes them from there.
    public static IReadOnlyList<PinDef> FromSession(Session.Shape shape) =>
        shape.Tabs.Where(e => e.Pin is not null).Select(e => new PinDef(e.PinId ?? Guid.NewGuid(), e.Pin!, e.Home ?? e.Url, e.Title) { Name = e.Name }).ToList();

    /// A pin's page, from the session: the one it was pinned at, or, for a pin from before pins kept theirs, where it
    /// was when it came back. Null for a tab that isn't a pin.
    public static string? HomeOf(Session.Entry entry) => entry.Pin is null ? null : entry.Home ?? entry.Url;

    /// A session's row with the space's pins as they are now (`defs`, Row's output): each pin's entry matched (by id,
    /// then by letter and page for one from before pins had ids, then by place), relettered and renamed, an entry made
    /// at its home for a pin new to this row, a pin taken away dropped; squares, then rows, then everything else, and
    /// the tab in front and the splits carried across. What a window reads at launch, so the oldest window's file
    /// keeps its pins and every window agrees with pins.json.
    public static Session.Shape InSession(Session.Shape shape, IReadOnlyList<PinDef> defs)
    {
        var pinned = Enumerable.Range(0, shape.Tabs.Count).Where(i => shape.Tabs[i].Pin is not null).Select(i => (int?)i).ToList();
        var loose = Enumerable.Range(0, shape.Tabs.Count).Where(i => shape.Tabs[i].Pin is null).ToList();
        var matched = Match(pinned, defs, i => shape.Tabs[i!.Value].PinId, i => shape.Tabs[i!.Value].Pin, i => shape.Tabs[i!.Value].Home);
        var tabs = new List<Session.Entry>();
        var places = new Dictionary<int, int>();
        foreach (var (def, found) in Tiered(matched, m => m.Def.Listed == true))
        {
            var listed = def.Listed == true ? true : (bool?)null;
            if (found is { } at) { places[at] = tabs.Count; tabs.Add(shape.Tabs[at] with { PinId = def.Id, Pin = def.Letter, Name = def.Name, Listed = listed, Home = def.Home }); }
            else tabs.Add(new Session.Entry(def.Home, def.Title) { Pin = def.Letter, Name = def.Name, Home = def.Home, PinId = def.Id, Listed = listed });
        }
        foreach (var at in loose) { places[at] = tabs.Count; tabs.Add(shape.Tabs[at]); }
        var active = shape.Active < 0 ? -1 : places.TryGetValue(shape.Active, out var a) ? a : tabs.FindIndex(e => e.Pin is null) is var first and >= 0 ? first : 0;
        var splits = shape.Splits.Where(s => s.Tabs.All(places.ContainsKey))
            .Select(s => s with { Tabs = s.Tabs.Select(t => places[t]).ToList(), Focused = s.Focused is { } f && places.TryGetValue(f, out var to) ? to : null }).ToList();
        return shape with { Tabs = tabs, Active = tabs.Count == 0 ? 0 : active, Splits = splits };
    }

    // A window's row.

    /// What makes this row's pinned tabs pins, in their order. A pin without an id is given one: the row handed back
    /// carries it, for the window to keep on its tab.
    public static (IReadOnlyList<PinDef> Defs, IReadOnlyList<PinnedTab> Row) Defs(IReadOnlyList<PinnedTab> row)
    {
        var defs = new List<PinDef>();
        var made = new List<PinnedTab>();
        foreach (var tab in row)
        {
            if (tab.Pin is not { } letter || tab.Private) { made.Add(tab); continue; }
            var kept = tab.PinId is null ? tab with { PinId = Guid.NewGuid() } : tab;
            made.Add(kept);
            defs.Add(new PinDef(kept.PinId!.Value, letter, kept.Home ?? kept.Url ?? "", kept.Title)
                { Name = kept.Name, Listed = kept.Listed ? true : null, Folder = kept.Listed && !kept.Everywhere ? kept.Folder : null, Pair = kept.Pair });
        }
        return (defs, made);
    }

    /// A row with the space's pins as they are now (`defs`, Row's output; `shared`, the ids of those every space
    /// shows): this window's own tab for each, relettered and in order, one Fresh for a pin new to this window, and
    /// the tab of a pin taken away in Gone, for the window to close. Squares, then rows, then the rest; with
    /// `folders` (pin folders on), the rows in the order their folders draw them.
    public static (IReadOnlyList<PinnedTab> Row, IReadOnlyList<Guid> Gone) Reconcile(IReadOnlyList<PinnedTab> row, IReadOnlyList<PinDef> defs, IReadOnlySet<Guid> shared, IReadOnlyList<PinFolder>? folders = null)
    {
        var pinned = row.Where(t => t.IsPin && !t.Private).ToList();
        var loose = row.Where(t => !t.IsPin || t.Private).ToList();
        var matched = Match(pinned, defs, t => t.PinId, t => t.Pin, t => t.Home);
        var made = matched.Select(m => (m.Tab ?? new PinnedTab(Guid.NewGuid()) { Fresh = true, Url = m.Def.Home, Title = m.Def.Title }) with
        {
            PinId = m.Def.Id, Everywhere = shared.Contains(m.Def.Id), Pin = m.Def.Letter, Name = m.Def.Name, Listed = m.Def.Listed == true, Folder = m.Def.Folder, Pair = m.Def.Pair, Home = m.Def.Home,
        }).ToList();
        var taken = matched.Where(m => m.Tab is not null).Select(m => m.Tab!.Id).ToHashSet();
        var gone = pinned.Where(t => !taken.Contains(t.Id)).Select(t => t.Id).ToList();
        IReadOnlyList<PinnedTab> tiered = [.. Tiered(made), .. loose];
        return (folders is null ? tiered : PinFolders.Arranged(tiered, folders), gone);
    }

    /// Each def with the one of `pinned` that is its pin here: by id, then (for one from before pins had ids) by
    /// letter and page, then the first without an id.
    private static List<(PinDef Def, T? Tab)> Match<T>(List<T> pinned, IReadOnlyList<PinDef> defs, Func<T, Guid?> id, Func<T, string?> letter, Func<T, string?> home)
    {
        var left = pinned.ToList();
        var made = new List<(PinDef, T?)>();
        foreach (var def in defs)
        {
            var at = left.FindIndex(t => id(t) == def.Id);
            if (at < 0) at = left.FindIndex(t => id(t) is null && letter(t) == def.Letter && (home(t) ?? "") == def.Home);
            if (at < 0) at = left.FindIndex(t => id(t) is null);
            if (at < 0) { made.Add((def, default)); continue; }
            made.Add((def, left[at]));
            left.RemoveAt(at);
        }
        return made;
    }

    private static IEnumerable<T> Tiered<T>(IEnumerable<T> pins, Func<T, bool> listed) { var all = pins.ToList(); return [.. all.Where(p => !listed(p)), .. all.Where(listed)]; }

    /// The squares ahead of the rows, each kind in its own order.
    public static IReadOnlyList<PinnedTab> Tiered(IEnumerable<PinnedTab> pins) => Tiered(pins, p => p.Listed).ToList();

    /// The shared pins ahead of the space's own, squares ahead of rows: a drag that crossed the line goes back to its side of it.
    public static IReadOnlyList<PinnedTab> KeepBlocks(IReadOnlyList<PinnedTab> row)
    {
        var pinned = row.Where(t => t.IsPin && !t.Private).ToList();
        return [.. Tiered([.. pinned.Where(t => t.Everywhere), .. pinned.Where(t => !t.Everywhere)]), .. row.Where(t => !t.IsPin || t.Private)];
    }

    /// The letter a pinned tab is reduced to, and what a tab shows in place of an icon it doesn't have yet: the host's
    /// first letter without www., a file's name's, "•" for nothing. `pageTitle`: one of Search's own pages, its title's.
    public static string Monogram(Uri? address, string? pageTitle = null)
    {
        if (pageTitle is not null) return pageTitle.Length == 0 ? "" : StringInfo.GetNextTextElement(pageTitle);
        if (address is { IsAbsoluteUri: true, IsFile: true })
        {
            var name = Path.GetFileName(address.LocalPath.TrimEnd('/', '\\')).Trim('/', '.');
            return name.Length == 0 ? "•" : StringInfo.GetNextTextElement(name).ToUpperInvariant();
        }
        var host = address is { IsAbsoluteUri: true } ? address.Host.Replace("www.", "", StringComparison.Ordinal) : "";
        return host.Length == 0 ? "•" : StringInfo.GetNextTextElement(host).ToUpperInvariant();
    }

    /// Typed into the square: its first character, uppercased. Null for nothing typed, which leaves the letter as it
    /// was: a pinned tab with nothing on it would be a blank square you could never identify.
    public static string? Letter(string typed) => typed.Trim() is { Length: > 0 } t ? StringInfo.GetNextTextElement(t).ToUpperInvariant() : null;

    /// The same page, give or take a trailing slash.
    public static bool SamePage(string one, string? other) => other is not null && one.TrimEnd('/') == other.TrimEnd('/');

    /// A double-click on the pin you are on: the page to go back to, the one it was pinned at. Null when it is already
    /// there (the double-click changes its letter then, as it always did) or isn't a pin.
    public static string? GoHome(PinnedTab tab) => tab.IsPin && tab.Home is { } home && !SamePage(home, tab.Url) ? home : null;

    /// Pin Tab: the letter from its address and its page as its home, a square unless `listed`. Null for a private
    /// tab. The window also takes it out of its group (TabGroups.Emptied) and its split (Splits.Leave), then Settled.
    public static PinnedTab? Pinned(PinnedTab tab, bool listed = false)
    {
        if (tab.Private) return null;
        if (tab.IsPin) return tab;
        return tab with { Pin = Monogram(tab.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var url) ? url : null), Home = tab.Url, Listed = listed };
    }

    /// Unpin: no letter, no home, no id, no folder. One every space shows leaves them all (Pins.Unshare). The window
    /// moves it to the head of the loose tabs (Unpinning).
    public static PinnedTab Unpinned(PinnedTab tab) => tab with { Pin = null, Listed = false, Folder = null, Home = null, PinId = null, Everywhere = false };

    /// Show as Row / Show as Square: it keeps its letter, page and home; a square is in no folder. Then Settled, with
    /// `first` for a row (the top of the rows; a square goes to the end of the squares).
    public static PinnedTab Listing(PinnedTab tab, bool listed) => tab with { Listed = listed, Folder = listed ? tab.Folder : null };

    /// A pin put in its place: the squares, then the rows, then everything else. `first`: at the head of its kind
    /// rather than the end. A square always goes at the end of the squares.
    public static IReadOnlyList<PinnedTab> Settled(IReadOnlyList<PinnedTab> row, Guid id, bool first = false)
    {
        var at = row.ToList().FindIndex(t => t.Id == id);
        if (at < 0) return row;
        var tab = row[at];
        var rest = row.Where(t => t.Id != id).ToList();
        var squares = rest.Count(t => t.IsPin && !t.Listed);
        rest.Insert(tab.Listed ? (first ? squares : rest.Count(t => t.IsPin)) : squares, tab);
        return rest;
    }

    /// An unpinned tab out of the pinned block, to the head of the loose tabs.
    public static IReadOnlyList<PinnedTab> Unpinning(IReadOnlyList<PinnedTab> row, Guid id)
    {
        if (row.FirstOrDefault(t => t.Id == id) is not { } tab) return row;
        var rest = row.Where(t => t.Id != id).ToList();
        rest.Insert(rest.Count(t => t.IsPin), tab);
        return rest;
    }

    /// The place a tab let go at `proposed` among the squares (or, `listed`, the rows) really takes: past the pins
    /// every space shows, which keep the head of the block, and never past the end.
    public static int Slot(IReadOnlyList<PinnedTab> row, PinnedTab tab, bool listed, int proposed, bool showsShared)
    {
        var tier = row.Where(t => t.IsPin && t.Listed == listed && t.Id != tab.Id).ToList();
        var shared = showsShared ? tier.TakeWhile(t => t.Everywhere).Count() : 0;
        return Math.Clamp(proposed, tab.Everywhere ? 0 : shared, tab.Everywhere ? shared : tier.Count);
    }

    /// A pin (already Pinned, or Listing'd to this kind) put at `slot` among the squares, or `listed` the rows.
    public static IReadOnlyList<PinnedTab> Dropped(IReadOnlyList<PinnedTab> row, PinnedTab tab, bool listed, int slot, bool showsShared)
    {
        var at = Slot(row, tab, listed, slot, showsShared);
        var pins = row.Where(t => t.IsPin && t.Id != tab.Id).ToList();
        var tier = pins.Where(t => t.Listed == listed).ToList();
        tier.Insert(Math.Min(at, tier.Count), tab with { Listed = listed });
        var squares = listed ? pins.Where(t => !t.Listed) : tier;
        var rows = listed ? tier : pins.Where(t => t.Listed);
        return [.. squares, .. rows, .. row.Where(t => !t.IsPin && t.Id != tab.Id)];
    }

    // The file.

    /// No shared pins, no "shared" key: the file stays what it was for anyone who never turns them on.
    public static byte[] Encode(IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>> bySpace, IReadOnlyList<PinDef> shared, IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>>? others = null) => SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        foreach (var (space, defs) in bySpace) WriteList(w, SwiftJson.Uuid(space), defs);
        if (shared.Count > 0) WriteList(w, SharedKey, shared);
        foreach (var (profile, defs) in others ?? new Dictionary<Guid, IReadOnlyList<PinDef>>()) if (defs.Count > 0) WriteList(w, SharedKey + ":" + SwiftJson.Uuid(profile), defs);
        w.WriteEndObject();
    });

    private static void WriteList(Utf8JsonWriter w, string key, IEnumerable<PinDef> defs)
    {
        w.WriteStartArray(key);
        foreach (var d in defs)
        {
            w.WriteStartObject();
            SwiftJson.WriteUuid(w, "id", d.Id);
            w.WriteString("letter", d.Letter);
            w.WriteString("home", d.Home);
            w.WriteString("title", d.Title);
            if (d.Name is { } name) w.WriteString("name", name);
            if (d.Listed is { } listed) w.WriteBoolean("listed", listed);
            if (d.Folder is { } folder) SwiftJson.WriteUuid(w, "folder", folder);
            if (d.Pair is { } pair) SwiftJson.WriteUuid(w, "pair", pair);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// Null when the file won't decode: every key's value has to be a list of pins, each with its id, letter, home and title.
    public static (Dictionary<Guid, IReadOnlyList<PinDef>> BySpace, IReadOnlyList<PinDef> Shared)? Decode(byte[] data)
    {
        if (ReadAll(data) is not { } saved) return null;
        var bySpace = new Dictionary<Guid, IReadOnlyList<PinDef>>();
        foreach (var (key, defs) in saved) if (SwiftJson.Uuid(key) is { } space) bySpace[space] = defs;
        return (bySpace, saved.TryGetValue(SharedKey, out var shared) ? shared : []);
    }

    /// Every profile's shared pins, Personal's under "shared".
    public static Dictionary<Guid, IReadOnlyList<PinDef>> SharedLists(byte[] data)
    {
        var made = new Dictionary<Guid, IReadOnlyList<PinDef>>();
        foreach (var (key, defs) in ReadAll(data) ?? new()) if (defs.Count > 0 && Profile(key) is { } profile) made[profile] = defs;
        return made;
    }

    private static Guid? Profile(string key) => key == SharedKey ? PersonalProfile : key.StartsWith(SharedKey + ":", StringComparison.Ordinal) ? SwiftJson.Uuid(key[(SharedKey.Length + 1)..]) : null;

    private static Dictionary<string, IReadOnlyList<PinDef>>? ReadAll(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var made = new Dictionary<string, IReadOnlyList<PinDef>>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) return null;
                var defs = new List<PinDef>();
                foreach (var item in property.Value.EnumerateArray()) if (ReadDef(item) is { } def) defs.Add(def); else return null;
                made[property.Name] = defs;
            }
            return made;
        }
        catch (JsonException) { return null; }
    }

    /// As Swift's synthesized decoder: the four words have to be there; a name, listed or folder that is there has to
    /// be the right kind (null is not there).
    private static PinDef? ReadDef(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id) return null;
        if (!e.TryGetProperty("letter", out var l) || SwiftJson.Text(l) is not { } letter || !e.TryGetProperty("home", out var h) || SwiftJson.Text(h) is not { } home
            || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title) return null;
        string? name = null;
        if (SwiftJson.Has(e, "name", out var n) && (name = SwiftJson.Text(n)) is null) return null;
        bool? listed = null;
        if (SwiftJson.Has(e, "listed", out var s) && (listed = SwiftJson.Bool(s)) is null) return null;
        Guid? folder = null;
        if (SwiftJson.Has(e, "folder", out var f) && (folder = SwiftJson.Uuid(f)) is null) return null;
        Guid? pair = null;
        if (SwiftJson.Has(e, "pair", out var p) && (pair = SwiftJson.Uuid(p)) is null) return null;
        return new PinDef(id, letter, home, title) { Name = name, Listed = listed, Folder = folder, Pair = pair };
    }
}
