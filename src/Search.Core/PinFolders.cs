using System.Text.Json;

namespace Search.Core;

// Folders among the pinned rows, as in Arc's pinned list (PinFolders.swift, BrowserPinFolders.swift): a folder holds
// pinned rows and other folders, folds and opens, and is the space's own. Only rows go in one (a square never does,
// nor a pin every space shows), and only while Pinned rows and Folders in pinned rows are both on are they drawn;
// turned off, the rows come back flat and their folders wait for the switch, as tab groups do.
//
// Which folder a pin is in is the pin's own (PinDef.Folder, in pins.json); the folders themselves, their names, which
// one holds which and whether they are folded, are kept in pin-folders.json, by space: {"<SPACE-UUID>": [{"id", "name",
// "parent"?, "collapsed", "feed"?, "items"?}]}. Inside a folder, or at the top of the rows, the folders come first, in
// their order, then the pins, in theirs. A live folder (LiveFolders.swift) is filled from a web feed: it holds the
// feed's posts, never a pin or a folder; its feed and posts are kept here so they make the trip intact.

/// One post of a live folder's feed: opened as a bookmark is, never a pin.
public sealed record FeedItem(Guid Id, string Title, string Link);

public sealed record PinFolder(Guid Id, string Name)
{
    /// The folder that holds this one; null at the top of the rows.
    public Guid? Parent { get; init; }
    public bool Collapsed { get; init; }
    /// A live folder's feed. Left out of the file for any other folder.
    public string? Feed { get; init; }
    /// A live folder's posts as last fetched.
    public IReadOnlyList<FeedItem>? Items { get; init; }

    public bool IsLive => Feed is not null;

    public bool Equals(PinFolder? other) => other is not null && Id == other.Id && Name == other.Name && Parent == other.Parent && Collapsed == other.Collapsed && Feed == other.Feed
        && (ReferenceEquals(Items, other.Items) || (Items is not null && other.Items is not null && Items.SequenceEqual(other.Items)));
    public override int GetHashCode() => HashCode.Combine(Id, Name, Parent, Collapsed, Feed);
}

/// The shape of the folders, worked out: what is drawn, what may go where. Pure.
public static class PinTree
{
    /// A folder, in a folder, in a folder: no deeper. A column 260 points wide does not have the room.
    public const int MaxDepth = 3;

    /// One line of the pinned rows: a folder's heading, a pin (by id), or a live folder's post, with how far in it is set (0 at the top).
    public abstract record Line(int Depth);
    public sealed record FolderLine(PinFolder Folder, int Depth) : Line(Depth);
    public sealed record PinLine(Guid Id, int Depth) : Line(Depth);
    public sealed record ItemLine(FeedItem Item, Guid Folder, int Depth) : Line(Depth);

    /// Where a folder carried by its heading goes: into `Parent` (null: the top), before the folder `Before` there, or
    /// with none after that level's other folders.
    public sealed record FolderPlace(Guid? Parent, Guid? Before = null);

    /// The folders as they can be trusted: each once; a parent that is gone, live, or the folder itself, or a folder
    /// that would hold itself, puts the folder at the top; one deeper than MaxDepth goes into the deepest folder
    /// above it that may hold it.
    public static IReadOnlyList<PinFolder> Sanitized(IReadOnlyList<PinFolder> folders)
    {
        var seen = new HashSet<Guid>();
        var made = folders.Where(f => seen.Add(f.Id)).ToList();
        var ids = made.Select(f => f.Id).ToHashSet();
        var live = made.Where(f => f.IsLive).Select(f => f.Id).ToHashSet();
        for (var i = 0; i < made.Count; i++)
            if (made[i].Parent is { } parent && (!ids.Contains(parent) || parent == made[i].Id || live.Contains(parent))) made[i] = made[i] with { Parent = null };
        // Cycles: walk up from each; a folder met twice is cut loose there.
        for (var i = 0; i < made.Count; i++)
        {
            var visited = new HashSet<Guid> { made[i].Id };
            var current = made[i].Parent;
            while (current is { } id && made.FindIndex(f => f.Id == id) is var at and >= 0)
            {
                if (!visited.Add(id)) { made[i] = made[i] with { Parent = null }; break; }
                current = made[at].Parent;
            }
        }
        // Too deep: up to the ancestor at MaxDepth - 1.
        for (var i = 0; i < made.Count; i++)
        {
            var chain = Ancestors(made[i].Id, made).ToList();
            while (chain.Count >= MaxDepth) { chain.RemoveAt(0); made[i] = made[i] with { Parent = chain[0] }; }
        }
        return made;
    }

    /// The folders above one, nearest first.
    public static IReadOnlyList<Guid> Ancestors(Guid id, IReadOnlyList<PinFolder> folders)
    {
        var chain = new List<Guid>();
        var current = folders.FirstOrDefault(f => f.Id == id)?.Parent;
        while (current is { } parent && !chain.Contains(parent) && folders.FirstOrDefault(f => f.Id == parent) is { } found) { chain.Add(parent); current = found.Parent; }
        return chain;
    }

    /// 1 for a folder at the top, 2 for one inside it, and so on.
    public static int Depth(Guid id, IReadOnlyList<PinFolder> folders) => Ancestors(id, folders).Count + 1;

    /// How many levels a folder and what it holds take: 1 for one holding no folder.
    public static int Height(Guid id, IReadOnlyList<PinFolder> folders)
    {
        var best = 1;
        var stack = new Stack<(Guid Id, int Level)>([(id, 1)]);
        var seen = new HashSet<Guid>();
        while (stack.TryPop(out var top))
        {
            if (!seen.Add(top.Id)) continue;
            best = Math.Max(best, top.Level);
            foreach (var child in folders.Where(f => f.Parent == top.Id)) stack.Push((child.Id, top.Level + 1));
        }
        return best;
    }

    /// Whether a folder may go into another (null: the top), staying within MaxDepth and never inside itself or a live one.
    public static bool CanMove(Guid id, Guid? parent, IReadOnlyList<PinFolder> folders)
    {
        if (parent is not { } into) return true;
        if (IsLive(into, folders) || into == id || Ancestors(into, folders).Contains(id)) return false;
        return Depth(into, folders) + Height(id, folders) <= MaxDepth;
    }

    /// Whether a new folder may be made inside `parent`.
    public static bool CanMake(Guid? parent, IReadOnlyList<PinFolder> folders) =>
        parent is not { } into || (folders.Any(f => f.Id == into) && !IsLive(into, folders) && Depth(into, folders) < MaxDepth);

    /// Whether a folder is a live one: no pin and no folder goes in it.
    public static bool IsLive(Guid? id, IReadOnlyList<PinFolder> folders) => id is { } at && folders.Any(f => f.Id == at && f.IsLive);

    /// A folder taken away: what it held, folders and pins, goes up into the folder that held it.
    public static (IReadOnlyList<PinFolder> Folders, IReadOnlyList<PinDef> Pins) Removing(Guid id, IReadOnlyList<PinFolder> folders, IReadOnlyList<PinDef> pins)
    {
        if (folders.FirstOrDefault(f => f.Id == id) is not { } gone) return (folders, pins);
        return (folders.Where(f => f.Id != id).Select(f => f.Parent == id ? f with { Parent = gone.Parent } : f).ToList(),
                pins.Select(p => p.Folder == id ? p with { Folder = gone.Parent } : p).ToList());
    }

    /// The pinned rows as drawn: from the top, each level's folders, each followed by what it holds unless it is
    /// folded, then that level's pins. A pin whose folder is gone (or is live) is at the top. `pins` are the rows only,
    /// in their order; `showing`, a pin drawn even inside a folded folder, as Arc keeps the page you are on in sight.
    public static IReadOnlyList<Line> Lines(IReadOnlyList<(Guid Id, Guid? Folder)> pins, IReadOnlyList<PinFolder> folders, Guid? showing = null)
    {
        var clean = Sanitized(folders);
        var ids = clean.Where(f => !f.IsLive).Select(f => f.Id).ToHashSet();
        Guid? Home(Guid? folder) => folder is { } f && ids.Contains(f) ? f : null;
        var made = new List<Line>();
        void Walk(Guid? parent, int depth, bool folded)
        {
            foreach (var folder in clean.Where(f => f.Parent == parent))
            {
                if (!folded) made.Add(new FolderLine(folder, depth));
                if (folder.IsLive)
                {
                    if (!folded && !folder.Collapsed) made.AddRange((folder.Items ?? []).Select(item => new ItemLine(item, folder.Id, depth + 1)));
                    continue;
                }
                Walk(folder.Id, depth + 1, folded || folder.Collapsed);
            }
            foreach (var pin in pins) if (Home(pin.Folder) == parent && (!folded || pin.Id == showing)) made.Add(new PinLine(pin.Id, depth));
        }
        Walk(null, 0, false);
        return made;
    }

    /// The pins in the order the lines draw them, for a row whose order on disk should match what is seen.
    public static IReadOnlyList<PinDef> Ordered(IReadOnlyList<PinDef> pins, IReadOnlyList<PinFolder> folders)
    {
        var byId = new Dictionary<Guid, PinDef>();
        foreach (var pin in pins) byId.TryAdd(pin.Id, pin);
        return OrderedIds(pins.Select(p => (p.Id, p.Folder)).ToList(), folders).Select(id => byId[id]).ToList();
    }

    /// The same, by id: every pin, each once, folded folders opened.
    public static IReadOnlyList<Guid> OrderedIds(IReadOnlyList<(Guid Id, Guid? Folder)> pins, IReadOnlyList<PinFolder> folders) =>
        Lines(pins, folders.Select(f => f with { Collapsed = false }).ToList()).OfType<PinLine>().Select(l => l.Id).ToList();

    /// The folders with `id` moved to `place`: its parent changed, and put before `place.Before` or after the other
    /// folders of that level.
    public static IReadOnlyList<PinFolder> Moving(Guid id, FolderPlace place, IReadOnlyList<PinFolder> folders)
    {
        var list = folders.ToList();
        var index = list.FindIndex(f => f.Id == id);
        if (index < 0 || place.Before == id) return folders;
        var folder = list[index] with { Parent = place.Parent };
        list.RemoveAt(index);
        var at = place.Before is { } before && list.FindIndex(f => f.Id == before) is var b and >= 0 ? b
            : list.FindLastIndex(f => f.Parent == place.Parent) is var last and >= 0 ? last + 1 : list.Count;
        list.Insert(at, folder);
        return list;
    }

    /// The folders less any not in `keeping` that hold no pin, directly or in a folder inside: an import that found
    /// every page of a folder already pinned makes no empty folder for it.
    public static IReadOnlyList<PinFolder> WithoutEmpty(IReadOnlyList<PinFolder> folders, IReadOnlySet<Guid> keeping, IReadOnlyList<PinDef> pins)
    {
        var full = new HashSet<Guid>();
        foreach (var id in pins.Select(p => p.Folder).OfType<Guid>().Distinct())
            if (folders.Any(f => f.Id == id)) { full.Add(id); full.UnionWith(Ancestors(id, folders)); }
        return folders.Where(f => keeping.Contains(f.Id) || full.Contains(f.Id) || f.IsLive).ToList();
    }
}

/// pin-folders.json: each space's folders, shared by every window as the pins are, plus the rules a window's pinned
/// rows follow with them.
public sealed class PinFolders
{
    public const string DefaultName = "New Folder";

    private readonly string _fileName;
    private readonly Dictionary<Guid, IReadOnlyList<PinFolder>> _bySpace = [];

    /// A space's folders changed in one window (or a live folder's fetch failed, with `always`); the others follow.
    public event Action<PinsChange>? Changed;

    public PinFolders(string file = "pin-folders.json") { _fileName = file; Load(); }

    private string FilePath => Store.File(_fileName);

    public IReadOnlyDictionary<Guid, IReadOnlyList<PinFolder>> BySpace => _bySpace;
    public IReadOnlyList<PinFolder> Folders(Guid space) => _bySpace.TryGetValue(space, out var folders) ? folders : [];

    private void Load()
    {
        byte[] data;
        try { data = File.ReadAllBytes(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (Decode(data) is { } all) foreach (var (space, folders) in all) _bySpace[space] = folders;
        else Store.Quarantine(FilePath);
    }

    /// A space's folders as they now are, sanitized, kept and passed to the other windows when they changed.
    /// `always`: passed on even unchanged, for a live folder whose fetch failed, to say so in its tooltip.
    public void Set(Guid space, IReadOnlyList<PinFolder> folders, object? from = null, bool always = false)
    {
        var clean = PinTree.Sanitized(folders);
        if (!Folders(space).SequenceEqual(clean))
        {
            if (clean.Count == 0) _bySpace.Remove(space); else _bySpace[space] = clean;
            Save();
        }
        else if (!always) return;
        Changed?.Invoke(new(from, space, null));
    }

    /// A space deleted: its folders go with it.
    public void Forget(Guid space) { if (_bySpace.Remove(space)) Save(); }

    private void Save()
    {
        var all = _bySpace.ToDictionary(p => p.Key, p => p.Value);
        Disk.Write(FilePath, () => Encode(all));
    }

    // A window's pinned rows and the folders.

    /// Whether a pin can go in a folder: a row of this space's own.
    public static bool TakesFolder(PinnedTab tab) => tab.IsPin && tab.Listed && !tab.Everywhere && !tab.Private;

    /// The pinned rows as the column draws them, folders and all: `rows` are the pinned rows (the listed pins, while
    /// rows show), `showing` the tab in front, drawn even inside a folded folder. A row every space shows is at the top.
    public static IReadOnlyList<PinTree.Line> RowLines(IReadOnlyList<PinnedTab> rows, IReadOnlyList<PinFolder> folders, Guid? showing = null) =>
        PinTree.Lines(rows.Select(t => (t.Id, t.Everywhere ? null : t.Folder)).ToList(), folders, showing);

    /// A row's pins with the rows in the order their folders draw them: the squares as they are, the rows every space
    /// shows first, then this space's own in folder order, then the rest.
    public static IReadOnlyList<PinnedTab> Arranged(IReadOnlyList<PinnedTab> row, IReadOnlyList<PinFolder> folders)
    {
        var pinned = row.Where(t => t.IsPin && !t.Private).ToList();
        var rows = pinned.Where(t => t.Listed).ToList();
        var own = rows.Where(t => !t.Everywhere).ToList();
        var byId = new Dictionary<Guid, PinnedTab>();
        foreach (var tab in own) byId.TryAdd(tab.Id, tab);
        var ordered = PinTree.OrderedIds(own.Select(t => (t.Id, t.Folder)).ToList(), folders).Select(id => byId[id]);
        return [.. pinned.Where(t => !t.Listed), .. rows.Where(t => t.Everywhere), .. ordered, .. row.Where(t => !t.IsPin || t.Private)];
    }

    /// New Folder: inside `parent`, or beside `holding` in the folder it is in (the window then moves it in with
    /// MovePin). Beside a pin in the deepest folder there is: one level up; asked for inside that folder: none. A
    /// name of nothing is "New Folder". Null when no folder may be made there, or `holding` can't go in one.
    public static (IReadOnlyList<PinFolder> Folders, Guid Made)? Made(IReadOnlyList<PinFolder> folders, string name = DefaultName, Guid? parent = null, PinnedTab? holding = null)
    {
        if (holding is not null && !TakesFolder(holding)) return null;
        var asked = parent is not null;
        var into = parent ?? holding?.Folder;
        if (into is { } at && !folders.Any(f => f.Id == at)) into = null;
        if (into is { } deep && !PinTree.CanMake(deep, folders))
        {
            if (asked) return null;
            into = folders.First(f => f.Id == deep).Parent;
        }
        if (!PinTree.CanMake(into, folders)) return null;
        var made = new PinFolder(Guid.NewGuid(), name.Trim() is { Length: > 0 } trimmed ? trimmed : DefaultName) { Parent = into };
        return ([.. folders, made], made.Id);
    }

    /// Renamed, trimmed and at most 200 characters; a name of nothing leaves it as it was. Null when nothing changes.
    public static IReadOnlyList<PinFolder>? Renamed(IReadOnlyList<PinFolder> folders, Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || !folders.Any(f => f.Id == id)) return null;
        return folders.Select(f => f.Id == id ? f with { Name = trimmed.Length > 200 ? trimmed[..200] : trimmed } : f).ToList();
    }

    /// Folded or opened, here and in every window. Null when it already is.
    public static IReadOnlyList<PinFolder>? Folded(IReadOnlyList<PinFolder> folders, Guid id, bool folded) =>
        folders.FirstOrDefault(f => f.Id == id) is { } folder && folder.Collapsed != folded ? folders.Select(f => f.Id == id ? f with { Collapsed = folded } : f).ToList() : null;

    public static IReadOnlyList<PinFolder>? Toggled(IReadOnlyList<PinFolder> folders, Guid id) => folders.FirstOrDefault(f => f.Id == id) is { } folder ? Folded(folders, id, !folder.Collapsed) : null;

    /// Delete Folder: what it held, folders and pins, goes up into the folder that held it (`Up`: what a pinned row
    /// in it gets as its folder now). No pin is closed. Null when there is no such folder.
    public static (IReadOnlyList<PinFolder> Folders, Guid? Up)? Deleted(IReadOnlyList<PinFolder> folders, Guid id) =>
        folders.FirstOrDefault(f => f.Id == id) is { } gone ? (PinTree.Removing(id, folders, []).Folders, gone.Parent) : null;

    /// A folder into another (null: the top), if it fits there. Null when it doesn't, or is already there.
    public static IReadOnlyList<PinFolder>? MovedInto(IReadOnlyList<PinFolder> folders, Guid id, Guid? parent) =>
        folders.FirstOrDefault(f => f.Id == id) is { } folder && folder.Parent != parent && PinTree.CanMove(id, parent, folders)
            ? folders.Select(f => f.Id == id ? f with { Parent = parent } : f).ToList() : null;

    /// A folder carried by its heading and let go: into another, out to the top, or before another folder, if it fits.
    public static IReadOnlyList<PinFolder>? MovedTo(IReadOnlyList<PinFolder> folders, Guid id, PinTree.FolderPlace place)
    {
        if (!PinTree.CanMove(id, place.Parent, folders)) return null;
        var moved = PinTree.Moving(id, place, folders);
        return moved.SequenceEqual(folders) ? null : moved;
    }

    /// A pinned row into a folder (null: the top; never a live one), beside `anchor` (after it with `after`), or at
    /// the end of the pinned rows; then the rows in folder order. Null for a tab that can't go in a folder.
    public static IReadOnlyList<PinnedTab>? MovePin(IReadOnlyList<PinnedTab> row, Guid tab, Guid? folder, IReadOnlyList<PinFolder> folders, Guid? anchor = null, bool after = false)
    {
        if (row.FirstOrDefault(t => t.Id == tab) is not { } moving || !TakesFolder(moving)) return null;
        var into = folder is { } id && folders.Any(f => f.Id == id && !f.IsLive) ? folder : null;
        var list = row.Where(t => t.Id != tab).ToList();
        moving = moving with { Folder = into };
        if (anchor is { } a && a != tab && list.FindIndex(t => t.Id == a) is var at and >= 0) list.Insert(after ? at + 1 : at, moving);
        else
        {
            var end = list.FindLastIndex(t => t.IsPin && t.Listed && !t.Private) is var last and >= 0 ? last + 1
                : list.FindIndex(t => !t.IsPin || t.Private) is var loose and >= 0 ? loose : list.Count;
            list.Insert(end, moving);
        }
        return Arranged(list, folders);
    }

    // The file.

    public static byte[] Encode(IReadOnlyDictionary<Guid, IReadOnlyList<PinFolder>> bySpace) => SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        foreach (var (space, folders) in bySpace)
        {
            if (folders.Count == 0) continue;
            w.WriteStartArray(SwiftJson.Uuid(space));
            foreach (var f in folders)
            {
                w.WriteStartObject();
                SwiftJson.WriteUuid(w, "id", f.Id);
                w.WriteString("name", f.Name);
                if (f.Parent is { } parent) SwiftJson.WriteUuid(w, "parent", parent);
                w.WriteBoolean("collapsed", f.Collapsed);
                if (f.Feed is { } feed) w.WriteString("feed", feed);
                if (f.Items is { } items)
                {
                    w.WriteStartArray("items");
                    foreach (var item in items)
                    {
                        w.WriteStartObject();
                        SwiftJson.WriteUuid(w, "id", item.Id);
                        w.WriteString("title", item.Title);
                        w.WriteString("link", item.Link);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    });

    /// Leniently: a space whose list won't read is a space with no folders, never a file that loses every other
    /// space's. Null only for a file that isn't a JSON object at all (the Mac read that as no folders and wrote over
    /// it; here it is set aside first).
    public static Dictionary<Guid, IReadOnlyList<PinFolder>>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var made = new Dictionary<Guid, IReadOnlyList<PinFolder>>();
            foreach (var property in document.RootElement.EnumerateObject())
                if (SwiftJson.Uuid(property.Name) is { } space && ReadList(property.Value) is { Count: > 0 } folders) made[space] = folders;
            return made;
        }
        catch (JsonException) { return null; }
    }

    private static List<PinFolder>? ReadList(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var folders = new List<PinFolder>();
        foreach (var item in list.EnumerateArray()) if (ReadFolder(item) is { } folder) folders.Add(folder); else return null;
        return folders;
    }

    /// Leniently, as a tab group: the id and name have to be there; a folder saved without its folded state is an open
    /// one, a parent that doesn't read is the top, and posts are read only for a live folder.
    private static PinFolder? ReadFolder(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id || !e.TryGetProperty("name", out var n) || SwiftJson.Text(n) is not { } name) return null;
        var feed = SwiftJson.Has(e, "feed", out var f) ? SwiftJson.Text(f) : null;
        return new PinFolder(id, name)
        {
            Parent = SwiftJson.Has(e, "parent", out var p) ? SwiftJson.Uuid(p) : null,
            Collapsed = SwiftJson.Has(e, "collapsed", out var c) && SwiftJson.Bool(c) == true,
            Feed = feed,
            Items = feed is not null && SwiftJson.Has(e, "items", out var items) ? ReadItems(items) : null,
        };
    }

    private static List<FeedItem>? ReadItems(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var items = new List<FeedItem>();
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title
                || !e.TryGetProperty("link", out var l) || SwiftJson.Text(l) is not { } link) return null;
            items.Add(new FeedItem(id, title, link));
        }
        return items;
    }
}
