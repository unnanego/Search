using System.Text;
using System.Text.Json;

namespace Search.Core;

// The merge behind Sync (SyncMerge.swift, rule for rule, and its file to the byte): plain values in, plain values
// out, nothing read or written here, so every rule can be tested on its own.
//
// What is synced is a flat map of items, key → a small JSON text. Each machine keeps, for every item, the value it
// last agreed on with a stamp (when it was set, in seconds since 1970) and the device that set it; a deletion is the
// same entry with no value, a tombstone, so "gone" travels as surely as "changed".
//
// A pass on one machine:
//   1. stamp: the items as it has them now, compared with what it last agreed (its base). What differs is an edit
//      made here since, stamped later than anything it has seen (so a machine whose clock runs behind still wins over
//      a value it already saw); what it agreed on and no longer has is a tombstone. A first pass (no base: joining)
//      stamps 0, so what is already in the folder wins where both have an item and nothing here is lost where only
//      this machine has it.
//   2. merge: this map and every other machine's file, item by item, the latest stamp winning, then the device id,
//      then a tombstone, then the larger text: the same answer everywhere whatever order the files are read in.
//   3. what changed is applied here, and the merged map is the new base and this machine's own file.
//
// Items this build can't read (a newer Search's, a Mac setting Windows has no place for, a damaged one) are carried
// along untouched and never turned into tombstones.
//
// The file, one per machine per scope: {"device","format":1,"items":{key:{"d","t","v"?}},"name"?,"scope","written"},
// keys sorted as Foundation sorts them, "/" escaped. Each item's text is written as the Mac writes it (SortedJson):
// sorted keys, "/" left as it is.

/// One item as agreed: its JSON (null for one deleted), when it was set and by which device.
public sealed record SyncEntry(string? V, double T, string D);

/// One machine's file in one scope's folder: only that machine writes it.
public sealed record SyncFile(int Format, string Device, string? Name, string Scope, double Written, IReadOnlyDictionary<string, SyncEntry> Items);

public static class SyncMerge
{
    /// The file format. A file of another is passed over, not misread.
    public const int Format = 1;
    /// How long a deletion is remembered: a machine away longer than this may bring back what was deleted meanwhile,
    /// and nothing worse.
    public const double TombstoneLife = 365 * 24 * 60 * 60;

    /// The kinds of item whose vanishing all at once is not taken as their deletion (see Stamp), and how many there
    /// must have been.
    public static readonly IReadOnlyList<string> WholeKinds = ["bm:", "kids:", "pin:", "order:pins:", "title:pin:", "space:"];
    public const int WholeKindFloor = 5;

    /// Whether `a` beats `b` for the same item. Total and the same on every machine.
    public static bool Wins(SyncEntry a, SyncEntry b)
    {
        if (a.T != b.T) return a.T > b.T;
        if (a.D != b.D) return SortedJson.CompareSwift(a.D, b.D) > 0;
        return (a.V, b.V) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            var (x, y) => SortedJson.CompareSwift(x, y) > 0,
        };
    }

    /// Every map's items, the winner of each.
    public static Dictionary<string, SyncEntry> Merge(IEnumerable<IReadOnlyDictionary<string, SyncEntry>> maps)
    {
        var made = new Dictionary<string, SyncEntry>();
        foreach (var map in maps)
            foreach (var (key, entry) in map)
                if (!made.TryGetValue(key, out var held) || Wins(entry, held)) made[key] = entry;
        return made;
    }

    /// The items as this machine has them now (`current`), written over what it last agreed (`base`). `understood`
    /// says which of base's items this build can read: only those can be found missing and deleted. `joining`: a
    /// first pass with a folder others already use; its items are stamped 0, so theirs win where both have one.
    public static Dictionary<string, SyncEntry> Stamp(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, SyncEntry> @base, string device, double now,
        bool joining = false, Func<string, string, bool>? understood = null)
    {
        // Later than anything seen, whatever this machine's clock says.
        var clock = joining ? 0 : Math.Max(now, (@base.Count == 0 ? 0 : @base.Values.Max(e => e.T)) + 0.001);
        var made = new Dictionary<string, SyncEntry>(@base);
        foreach (var (key, value) in current)
            if (!@base.TryGetValue(key, out var held) || held.V != value) made[key] = new(value, clock, device);
        // A whole kind gone at once (every bookmark, every pin) is a file that didn't read or a browser reset, far
        // likelier than a choice: those are not deleted everywhere, they come back from the folder.
        var lost = WholeKinds.Where(kind => !current.Keys.Any(k => k.StartsWith(kind, StringComparison.Ordinal))
            && @base.Count(p => p.Key.StartsWith(kind, StringComparison.Ordinal) && p.Value.V is not null) >= WholeKindFloor).ToList();
        foreach (var (key, entry) in @base)
        {
            if (entry.V is not { } value || current.ContainsKey(key) || !(understood?.Invoke(key, value) ?? true)
                || lost.Any(kind => key.StartsWith(kind, StringComparison.Ordinal))) continue;
            made[key] = new(null, clock, device);
        }
        return made;
    }

    /// One pass, as every machine runs it: this one's items stamped over its base (null before its first pass),
    /// merged with the other machines' maps, old deletions forgotten. What comes back is the new base, this machine's
    /// file, and (its live items) what this machine should now have.
    public static Dictionary<string, SyncEntry> Pass(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, SyncEntry>? @base,
        IReadOnlyList<IReadOnlyDictionary<string, SyncEntry>> theirs, string device, double now, Func<string, string, bool>? understood = null)
    {
        var mine = Stamp(current, @base ?? new Dictionary<string, SyncEntry>(), device, now, joining: @base is null && theirs.Count > 0, understood);
        return Prune(Merge([mine, .. theirs]), now);
    }

    /// The items that are there, without the tombstones.
    public static Dictionary<string, string> Live(IReadOnlyDictionary<string, SyncEntry> map) =>
        map.Where(p => p.Value.V is not null).ToDictionary(p => p.Key, p => p.Value.V!);

    /// The keys deleted.
    public static HashSet<string> Tombstoned(IReadOnlyDictionary<string, SyncEntry> map) => map.Where(p => p.Value.V is null).Select(p => p.Key).ToHashSet();

    /// Deletions older than TombstoneLife forgotten.
    public static Dictionary<string, SyncEntry> Prune(IReadOnlyDictionary<string, SyncEntry> map, double now) =>
        map.Where(p => p.Value.V is not null || p.Value.T >= now - TombstoneLife).ToDictionary(p => p.Key, p => p.Value);

    /// Two maps alike, as Swift's == on them.
    public static bool Same(IReadOnlyDictionary<string, SyncEntry>? a, IReadOnlyDictionary<string, SyncEntry>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var o) && o == p.Value));

    public static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var o) && o == p.Value);

    /// A list's order applied to the ids that are there: the order's first, as it has them, then the rest in
    /// `fallback`'s order, then any left by id. An item added on one machine while another reordered is kept, at the
    /// end, and every machine ends with the same list.
    public static List<string> Ordered(IReadOnlySet<string> ids, IEnumerable<string> order, IEnumerable<string>? fallback = null)
    {
        var made = new List<string>();
        var seen = new HashSet<string>();
        foreach (var id in order.Concat(fallback ?? []))
            if (ids.Contains(id) && seen.Add(id)) made.Add(id);
        return [.. made, .. ids.Where(id => !seen.Contains(id)).Order(SortedJson.SwiftOrder)];
    }

    // The file.

    /// Another machine's file, if it is one this build can read for this scope: damaged, foreign, other-format and
    /// wrong-folder files are null, as Swift's decoder sees them, and an entry with a stamp below 0 or no device is
    /// left out.
    public static SyncFile? Decode(byte[] data, string scope)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var f) || SwiftJson.Int(f) is not { } format || !root.TryGetProperty("device", out var d) || SwiftJson.Text(d) is not { } device
                || !root.TryGetProperty("scope", out var s) || SwiftJson.Text(s) is not { } inScope || !root.TryGetProperty("written", out var w) || SwiftJson.Number(w) is not { } written
                || !root.TryGetProperty("items", out var i) || ReadItems(i) is not { } items) return null;
            string? name = null;
            if (SwiftJson.Has(root, "name", out var n) && (name = SwiftJson.Text(n)) is null) return null;
            if (format != Format || inScope != scope || device.Length == 0) return null;
            return new(format, device, name, inScope, written, items.Where(p => double.IsFinite(p.Value.T) && p.Value.T >= 0 && p.Value.D.Length > 0).ToDictionary(p => p.Key, p => p.Value));
        }
        catch (JsonException) { return null; }
    }

    public static byte[] Encode(SyncFile file) => Encoding.UTF8.GetBytes(SortedJson.Object(
    [
        ("device", SortedJson.String(file.Device, true)), ("format", SortedJson.Number(file.Format)), ("items", Items(file.Items)),
        ("name", file.Name is { } name ? SortedJson.String(name, true) : null), ("scope", SortedJson.String(file.Scope, true)), ("written", SortedJson.Number(file.Written)),
    ], escapeSlashes: true));

    /// What a machine last agreed, per scope, in Search's own folder: the items alone, as the file has them.
    public static byte[] EncodeState(IReadOnlyDictionary<string, SyncEntry> map) => Encoding.UTF8.GetBytes(Items(map));

    public static Dictionary<string, SyncEntry>? DecodeState(byte[] data)
    {
        try { using var document = JsonDocument.Parse(data); return ReadItems(document.RootElement); }
        catch (JsonException) { return null; }
    }

    private static string Items(IReadOnlyDictionary<string, SyncEntry> map) => SortedJson.Object(map.Select(p => (p.Key, (string?)SortedJson.Object(
        [("d", SortedJson.String(p.Value.D, true)), ("t", SortedJson.Number(p.Value.T)), ("v", p.Value.V is { } v ? SortedJson.String(v, true) : null)], true))), escapeSlashes: true);

    /// As Swift decodes [String: SyncEntry]: every entry an object with a number t and a string d, and a v that is
    /// there a string.
    private static Dictionary<string, SyncEntry>? ReadItems(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var made = new Dictionary<string, SyncEntry>();
        foreach (var p in e.EnumerateObject())
        {
            var x = p.Value;
            if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("t", out var t) || SwiftJson.Number(t) is not { } stamp
                || !x.TryGetProperty("d", out var d) || SwiftJson.Text(d) is not { } device) return null;
            string? value = null;
            if (SwiftJson.Has(x, "v", out var v) && (value = SwiftJson.Text(v)) is null) return null;
            made[p.Name] = new(value, stamp, device);
        }
        return made;
    }

    // An item's text.

    /// A JSON text as an element, or null for one that doesn't parse. Top-level strings, numbers and lists are fine,
    /// as JSONDecoder takes them.
    internal static T? Value<T>(string text, Func<JsonElement, T?> read) where T : class
    {
        try { using var document = JsonDocument.Parse(text); return read(document.RootElement); }
        catch (JsonException) { return null; }
    }

    internal static string? Text(string text) => Value(text, SwiftJson.Text);

    internal static List<string>? Strings(string text) => Value(text, e =>
        e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String) ? e.EnumerateArray().Select(i => i.GetString()!).ToList() : null);
}

// What each kind of thing is, as items.

/// The parts of a space that sync: what it is called and looks like, and whether it has sign-ins of its own. Never
/// its cookies, sign-ins, tabs or downloads folder.
public sealed record SyncedSpace(string Name, int Colour, string? Icon = null, bool? SharesSignIns = null)
{
    public string Text() => SortedJson.Object([("colour", SortedJson.Number(Colour)), ("icon", Icon is null ? null : SortedJson.String(Icon)), ("name", SortedJson.String(Name)),
        ("sharesSignIns", SharesSignIns is { } shares ? SortedJson.Bool(shares) : null)]);

    public static SyncedSpace? Read(string text) => SyncMerge.Value(text, e =>
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("name", out var n) || SwiftJson.Text(n) is not { } name || !e.TryGetProperty("colour", out var c) || SwiftJson.Int(c) is not { } colour) return null;
        string? icon = null;
        if (SwiftJson.Has(e, "icon", out var i) && (icon = SwiftJson.Text(i)) is null) return null;
        bool? shares = null;
        if (SwiftJson.Has(e, "sharesSignIns", out var s) && (shares = SwiftJson.Bool(s)) is null) return null;
        return new SyncedSpace(name, colour, icon, shares);
    });
}

/// The parts of a pin that sync. Its title is the page's, which changes as you browse; it goes once, beside it, for a
/// machine that makes the pin anew.
public sealed record SyncedPin(string Letter, string Home, string? Name = null, bool? Listed = null)
{
    public string Text() => SortedJson.Object([("home", SortedJson.String(Home)), ("letter", SortedJson.String(Letter)), ("listed", Listed is { } l ? SortedJson.Bool(l) : null),
        ("name", Name is null ? null : SortedJson.String(Name))]);

    public static SyncedPin? Read(string text) => SyncMerge.Value(text, e =>
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("letter", out var l) || SwiftJson.Text(l) is not { } letter || !e.TryGetProperty("home", out var h) || SwiftJson.Text(h) is not { } home) return null;
        string? name = null;
        if (SwiftJson.Has(e, "name", out var n) && (name = SwiftJson.Text(n)) is null) return null;
        bool? listed = null;
        if (SwiftJson.Has(e, "listed", out var s) && (listed = SwiftJson.Bool(s)) is null) return null;
        return new SyncedPin(letter, home, name, listed);
    });
}

/// A bookmark or a folder (no url), without its place: the folder that holds it lists it.
public sealed record SyncedBookmark(string Title, string? Url = null)
{
    public string Text() => SortedJson.Object([("title", SortedJson.String(Title)), ("url", Url is null ? null : SortedJson.String(Url))]);

    public static SyncedBookmark? Read(string text) => SyncMerge.Value(text, e =>
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title) return null;
        string? url = null;
        if (SwiftJson.Has(e, "url", out var u) && (url = SwiftJson.Text(u)) is null) return null;
        return new SyncedBookmark(title, url);
    });
}

public static class SyncItems
{
    // Spaces.

    public const string SpacesOrder = "order:spaces";

    public static string SpaceKey(Guid id) => "space:" + SwiftJson.Uuid(id);
    /// A space's gradient, an item of its own beside the space's: a Search from before gradients doesn't read it, so
    /// it carries it along as it is. Only a space with a gradient has one.
    public static string GradientKey(Guid id) => "gradient:" + SwiftJson.Uuid(id);
    /// How strongly a space's colour washes the chrome, an item of its own in the same way. Only a space that isn't
    /// Subtle has one.
    public static string StrengthKey(Guid id) => "strength:" + SwiftJson.Uuid(id);

    public static string GradientText(SpaceGradient g) => SortedJson.Object([("angle", SortedJson.Number(g.Angle)), ("colours", SortedJson.Strings(g.Colours)), ("grain", SortedJson.Bool(g.Grain))]);
    public static SpaceGradient? Gradient(string text) => SyncMerge.Value(text, SpaceGradient.Read);

    /// A strength item's text as one this build knows; null for one it doesn't (a newer Search's), carried along.
    public static SpaceStrength? Strength(string text) => SyncMerge.Text(text) switch { "subtle" => SpaceStrength.Subtle, "rich" => SpaceStrength.Rich, "vivid" => SpaceStrength.Vivid, _ => null };

    /// One profile's spaces, as items. Whether a space has sign-ins of its own is what was agreed when it was first
    /// synced (`agreed`), never changed after: a store can't change under a space's open tabs.
    public static Dictionary<string, string> Spaces(IReadOnlyList<Space> list, IReadOnlyDictionary<string, string>? agreed = null)
    {
        var made = new Dictionary<string, string>();
        foreach (var space in list)
        {
            var was = agreed is not null && agreed.TryGetValue(SpaceKey(space.Id), out var text) ? SyncedSpace.Read(text) : null;
            made[SpaceKey(space.Id)] = new SyncedSpace(space.Name, space.Colour, space.Icon, was is not null ? was.SharesSignIns : space.SharesSignIns).Text();
            if (space.Gradient is { Usable: true } gradient) made[GradientKey(space.Id)] = GradientText(gradient);
            if (space.Strength is { } strength && strength != SpaceStrength.Subtle) made[StrengthKey(space.Id)] = SortedJson.String(strength.Raw());
        }
        if (list.Count > 0) made[SpacesOrder] = SortedJson.Strings(list.Select(s => SwiftJson.Uuid(s.Id)));
        return made;
    }

    /// The spaces the merged items say there are: names, colours and icons as merged, in the merged order; what is
    /// only this machine's (the downloads folder) kept; whether one has its own sign-ins never changed once it is
    /// made. `home` is never taken away.
    public static List<Space> ApplySpaces(IReadOnlyDictionary<string, string> live, IReadOnlyList<Space> local, Guid profile, Guid home)
    {
        var found = new Dictionary<string, SyncedSpace>();
        foreach (var (key, text) in live)
            if (key.StartsWith("space:", StringComparison.Ordinal) && SwiftJson.Uuid(key[6..]) is not null && SyncedSpace.Read(text) is { } space) found[key[6..]] = space;
        var ids = found.Keys.ToHashSet();
        // A space this machine has that the items don't read stays; the home always does.
        foreach (var space in local)
            if (space.Id == home || (live.TryGetValue(SpaceKey(space.Id), out var text) && SyncedSpace.Read(text) is null)) ids.Add(SwiftJson.Uuid(space.Id));
        var order = live.TryGetValue(SpacesOrder, out var o) ? SyncMerge.Strings(o) ?? [] : [];
        var made = new List<Space>();
        foreach (var text in SyncMerge.Ordered(ids, order, local.Select(s => SwiftJson.Uuid(s.Id))))
        {
            if (SwiftJson.Uuid(text) is not { } id) continue;
            var mine = local.FirstOrDefault(s => s.Id == id);
            var space = mine ?? new Space(id, "") { Profile = profile == Search.Core.Spaces.PersonalProfile ? null : profile };
            if (found.TryGetValue(text, out var synced))
                space = space with
                {
                    Name = synced.Name, Colour = synced.Colour, Icon = synced.Icon, SharesSignIns = mine is null ? synced.SharesSignIns : space.SharesSignIns,
                    Gradient = live.TryGetValue(GradientKey(id), out var g) && Gradient(g) is { Usable: true } gradient ? gradient : null,
                    Strength = live.TryGetValue(StrengthKey(id), out var s) && Strength(s) is { } strength && strength != SpaceStrength.Subtle ? strength : null,
                };
            if (space.Name.Length == 0) space = space with { Name = "Space" };
            made.Add(space);
        }
        return made;
    }

    // Pins.

    public const string SharedSpace = "shared";

    public static string PinKey(string space, Guid id) => $"pin:{space}:{SwiftJson.Uuid(id)}";
    public static string PinOrder(string space) => $"order:pins:{space}";
    public static string PinTitle(Guid id) => $"title:pin:{SwiftJson.Uuid(id)}";

    /// A profile's pins as items: each space's own, and the ones every space shows. A pin's title is the one already
    /// agreed, if there is one.
    public static Dictionary<string, string> Pins(IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>> bySpace, IReadOnlyList<PinDef> shared, IReadOnlyDictionary<string, string>? agreed = null)
    {
        var made = new Dictionary<string, string>();
        void Add(IReadOnlyList<PinDef> defs, string space)
        {
            foreach (var def in defs)
            {
                made[PinKey(space, def.Id)] = new SyncedPin(def.Letter, def.Home, def.Name, def.Listed).Text();
                made[PinTitle(def.Id)] = agreed is not null && agreed.TryGetValue(PinTitle(def.Id), out var title) ? title : SortedJson.String(def.Title);
            }
            if (defs.Count > 0) made[PinOrder(space)] = SortedJson.Strings(defs.Select(d => SwiftJson.Uuid(d.Id)));
        }
        foreach (var (space, defs) in bySpace) Add(defs, SwiftJson.Uuid(space));
        Add(shared, SharedSpace);
        return made;
    }

    /// The pins the merged items say each of `spaces` has, and the shared ones. A pin in both the shared list and a
    /// space's (shared on one machine while edited on another) is shared. Titles: the one agreed, else the one this
    /// machine had, else the page's address. A pin that stays in its space keeps the folder it is in here: folders
    /// don't sync, and the Mac's merge would drop it.
    public static (Dictionary<Guid, IReadOnlyList<PinDef>> BySpace, IReadOnlyList<PinDef> Shared) ApplyPins(IReadOnlyDictionary<string, string> live, IReadOnlyList<Guid> spaces,
        IReadOnlyDictionary<Guid, IReadOnlyList<PinDef>> local, IReadOnlyList<PinDef> localShared)
    {
        var known = local.Values.SelectMany(d => d).Concat(localShared).ToList();
        List<PinDef> Defs(string space, IReadOnlyList<PinDef> fallback)
        {
            var prefix = $"pin:{space}:";
            var found = new Dictionary<string, SyncedPin>();
            foreach (var (key, text) in live)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && SwiftJson.Uuid(key[prefix.Length..]) is not null && SyncedPin.Read(text) is { } pin) found[key[prefix.Length..]] = pin;
            var order = live.TryGetValue(PinOrder(space), out var o) ? SyncMerge.Strings(o) ?? [] : [];
            var made = new List<PinDef>();
            foreach (var text in SyncMerge.Ordered(found.Keys.ToHashSet(), order, fallback.Select(d => SwiftJson.Uuid(d.Id))))
            {
                if (SwiftJson.Uuid(text) is not { } id || !found.TryGetValue(text, out var pin)) continue;
                var title = (live.TryGetValue(PinTitle(id), out var t) ? SyncMerge.Text(t) : null) ?? known.FirstOrDefault(d => d.Id == id)?.Title ?? pin.Home;
                made.Add(new PinDef(id, pin.Letter, pin.Home, title) { Name = pin.Name, Listed = pin.Listed, Folder = fallback.FirstOrDefault(d => d.Id == id)?.Folder });
            }
            return made;
        }
        var shared = Defs(SharedSpace, localShared).Select(d => d with { Folder = null }).ToList();
        var sharedIds = shared.Select(d => d.Id).ToHashSet();
        var bySpace = new Dictionary<Guid, IReadOnlyList<PinDef>>();
        foreach (var space in spaces)
        {
            var own = Defs(SwiftJson.Uuid(space), local.TryGetValue(space, out var l) ? l : []).Where(d => !sharedIds.Contains(d.Id)).ToList();
            if (own.Count > 0) bySpace[space] = own;
        }
        return (bySpace, shared);
    }

    // Bookmarks.

    public const string RootFolder = "root";

    public static string BookmarkKey(Guid id) => "bm:" + SwiftJson.Uuid(id);
    public static string ChildrenKey(string folder) => "kids:" + folder;

    /// A bookmarks tree as items: each bookmark and folder, and each folder's list of what it holds, the top level
    /// under "root".
    public static Dictionary<string, string> Bookmarks(IReadOnlyList<Bookmark> roots)
    {
        var made = new Dictionary<string, string>();
        void Walk(IReadOnlyList<Bookmark> nodes, string folder)
        {
            if (nodes.Count > 0) made[ChildrenKey(folder)] = SortedJson.Strings(nodes.Select(n => SwiftJson.Uuid(n.Id)));
            foreach (var node in nodes)
            {
                made[BookmarkKey(node.Id)] = new SyncedBookmark(node.Title, node.Url).Text();
                if (node.IsFolder) Walk(node.Children ?? [], SwiftJson.Uuid(node.Id));
            }
        }
        Walk(roots, RootFolder);
        return made;
    }

    /// The tree the merged items say there is. A bookmark no folder lists any more (its folder deleted on one machine
    /// while it was added there on another, or two folders moved into each other) goes at the end of the top level
    /// rather than being lost.
    public static List<Bookmark> ApplyBookmarks(IReadOnlyDictionary<string, string> live)
    {
        var nodes = new Dictionary<string, SyncedBookmark>();
        foreach (var (key, text) in live)
            if (key.StartsWith("bm:", StringComparison.Ordinal) && SwiftJson.Uuid(key[3..]) is not null && SyncedBookmark.Read(text) is { } node) nodes[key[3..]] = node;
        var placed = new HashSet<string>();
        List<string> Kids(string folder) => live.TryGetValue(ChildrenKey(folder), out var text) ? SyncMerge.Strings(text) ?? [] : [];
        Bookmark Made(string id, Guid uuid, SyncedBookmark node) => new(uuid, node.Title, node.Url, node.Url is null ? Build(id) : null);
        List<Bookmark> Build(string folder)
        {
            var made = new List<Bookmark>();
            foreach (var id in Kids(folder))
            {
                if (!nodes.TryGetValue(id, out var node) || placed.Contains(id) || SwiftJson.Uuid(id) is not { } uuid) continue;
                placed.Add(id);
                made.Add(Made(id, uuid, node));
            }
            return made;
        }
        var roots = Build(RootFolder);
        // What no folder reached: each, with what it holds, at the end.
        foreach (var id in nodes.Keys.Order(SortedJson.SwiftOrder).ToList())
        {
            if (placed.Contains(id) || SwiftJson.Uuid(id) is not { } uuid) continue;
            placed.Add(id);
            roots.Add(Made(id, uuid, nodes[id]));
        }
        return roots;
    }

    /// Whether this build can read an item: only those can be found missing here and so deleted everywhere. A
    /// setting counts only if Windows has a place for it (SyncSettings.Mapped); the Mac's others ride along.
    public static bool Understood(string key, string text)
    {
        if (key.StartsWith("space:", StringComparison.Ordinal)) return SyncedSpace.Read(text) is not null;
        if (key.StartsWith("gradient:", StringComparison.Ordinal)) return Gradient(text) is { Usable: true };
        if (key.StartsWith("strength:", StringComparison.Ordinal)) return Strength(text) is not null;
        if (key.StartsWith("pin:", StringComparison.Ordinal)) return SyncedPin.Read(text) is not null;
        if (key.StartsWith("bm:", StringComparison.Ordinal)) return SyncedBookmark.Read(text) is not null;
        if (key.StartsWith("order:", StringComparison.Ordinal) || key.StartsWith("kids:", StringComparison.Ordinal)) return SyncMerge.Strings(text) is not null;
        if (key.StartsWith("title:pin:", StringComparison.Ordinal)) return SyncMerge.Text(text) is not null;
        if (key.StartsWith("set:", StringComparison.Ordinal)) return SyncSettings.Mapped.Contains(key[4..]);
        return false;
    }
}
