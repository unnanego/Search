using System.Text.Json;

namespace Search.Core;

// What was open last time (Session.swift): a list of addresses and their names, and which one you were looking at.
// Nothing else, because everything else is either on the page or in the history file next door.
//
// The file is the Mac's, key for key (see SwiftJson), so a session can move between a Mac and a PC. The groups and
// splits are the parts an older or newer version may not agree on, so they are read leniently: a value that doesn't
// make sense is taken for no groups (or no splits) at all, never for a file that won't decode. That would put the
// whole session in quarantine and bring back not a single tab.

/// A named section of ordinary tabs in one space's tab layout (TabGroup.swift). Its tabs remain ordinary tabs; their
/// own group ids describe membership.
public sealed record TabGroup(Guid Id, string Name, bool Collapsed = false)
{
    public int? Colour { get; init; }
}

/// How a split's pages share the stage: side by side (the first on the left), or one above the other.
public enum SplitAxis { Horizontal, Vertical }

public static class Session
{
    /// The first space's id (Space.firstID): its session is the one there always was, session.json.
    public static readonly Guid FirstSpace = new("00000000-0000-0000-0000-000000000001");

    public sealed record Entry(string Url, string Title)
    {
        public string? Pin { get; init; }
        /// The name you gave the tab, when you gave it one.
        public string? Name { get; init; }
        /// A pin's own page, the one it was pinned at.
        public string? Home { get; init; }
        /// The group this ordinary tab belongs to, if any.
        public Guid? GroupId { get; init; }
        /// A pin's identity, the same in every window.
        public Guid? PinId { get; init; }
        /// When you last left the tab, so tabs that close themselves count the night Search was quit.
        public DateTimeOffset? Touched { get; init; }
        /// When it was last let go of. Null when it never was.
        public DateTimeOffset? LetGo { get; init; }
        /// A pin kept as a row. Null for a square.
        public bool? Listed { get; init; }
        /// Silenced by hand from the tab's speaker or menu.
        public bool Muted { get; init; }
        /// An open tab's identity while Same tabs in every window is on.
        public Guid? TwinId { get; init; }
        /// The id of the tab this entry is, while it sits in a small window.
        public Guid? Lifted { get; init; }
    }

    /// `Groups` is null in sessions written before tab groups existed; both it and `Splits` are written whether or
    /// not the feature is on, so turning it off loses nothing.
    public sealed record Shape(IReadOnlyList<Entry> Tabs, int Active)
    {
        public IReadOnlyList<TabGroup>? Groups { get; init; }
        /// Indices in `Tabs`, so restored tabs can have new live identities.
        public IReadOnlyList<Split> Splits { get; init; } = [];
        public static Shape Empty => new([], 0);
    }

    /// A split, its pages by their place in `Tabs`.
    public sealed record Split(IReadOnlyList<int> Tabs, IReadOnlyList<double> Sizes)
    {
        public SplitAxis Axis { get; init; } = SplitAxis.Horizontal;
        /// The page last focused, by its place in `Tabs`.
        public int? Focused { get; init; }
    }

    /// The first space's is the session there always was; each other space keeps its own beside it.
    private static string File(Guid space) => Store.File(space == FirstSpace ? "session.json" : $"session-{SwiftJson.Uuid(space)}.json");

    /// Settings › General › Start with a fresh window, at launch, before any window reads its row (see Fresh).
    public static void StartFresh(IEnumerable<Guid> spaces)
    {
        foreach (var space in spaces)
        {
            var shape = Read(space);
            var left = Fresh(shape);
            if (left.Tabs.Count == shape.Tabs.Count && shape.Active < 0) continue;
            Write(left, now: true, space);
        }
    }

    /// A row as a fresh start leaves it: its pins, whole; the tabs in its groups, with the groups; a tab you named;
    /// and nothing else. A split stays only when all its pages do. No tab is marked as in front, so the window opens
    /// on an empty tab beside the pins.
    public static Shape Fresh(Shape shape)
    {
        var groups = shape.Groups ?? [];
        var grouped = groups.Select(g => g.Id).ToHashSet();
        var places = new Dictionary<int, int>();
        var tabs = new List<Entry>();
        for (var index = 0; index < shape.Tabs.Count; index++)
        {
            var entry = shape.Tabs[index];
            if (entry.Pin is null && entry.Name is null && !(entry.GroupId is { } g && grouped.Contains(g))) continue;
            places[index] = tabs.Count;
            tabs.Add(entry);
        }
        var keptGroups = groups.Where(group => tabs.Any(t => t.GroupId == group.Id)).ToList();
        var splits = new List<Split>();
        foreach (var pair in shape.Splits)
        {
            var moved = pair.Tabs.Where(places.ContainsKey).Select(t => places[t]).ToList();
            if (moved.Count != pair.Tabs.Count) continue;
            splits.Add(pair with { Tabs = moved, Focused = pair.Focused is { } f && places.TryGetValue(f, out var at) ? at : null });
        }
        return new Shape(tabs, -1) { Groups = keptGroups.Count == 0 ? null : keptGroups, Splits = splits };
    }

    public static void Erase(Guid space)
    {
        if (space == FirstSpace) return;
        try { System.IO.File.Delete(File(space)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static Shape Read(Guid? space = null)
    {
        var file = File(space ?? FirstSpace);
        byte[] data;
        try { data = System.IO.File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Shape.Empty; }
        if (Decode(data) is { } shape) return shape;
        // A file that's there but won't decode is not the same as no file: something wrote it, and overwriting it
        // on the next save without a trace is how yesterday's tabs actually disappear.
        Store.Quarantine(file);
        return Shape.Empty;
    }

    /// `now` writes on the calling thread: quitting doesn't wait for a background queue, and a session handed to
    /// one on the way out is a session that may never reach the disk. One after another, the newest last (see Disk).
    public static Task Write(Shape shape, bool now = false, Guid? space = null) => Disk.Write(File(space ?? FirstSpace), () => Encode(shape), now);

    // The file.

    public static byte[] Encode(Shape shape) => SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("tabs");
        foreach (var entry in shape.Tabs) Write(w, entry);
        w.WriteEndArray();
        w.WriteNumber("active", shape.Active);
        if (shape.Groups is { } groups)
        {
            w.WriteStartArray("groups");
            foreach (var group in groups) Write(w, group);
            w.WriteEndArray();
        }
        w.WriteStartArray("splits");
        foreach (var split in shape.Splits) Write(w, split);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static void Write(Utf8JsonWriter w, Entry e)
    {
        w.WriteStartObject();
        w.WriteString("url", e.Url);
        w.WriteString("title", e.Title);
        if (e.Pin is { } pin) w.WriteString("pin", pin);
        if (e.Name is { } name) w.WriteString("name", name);
        if (e.Home is { } home) w.WriteString("home", home);
        if (e.GroupId is { } groupId) SwiftJson.WriteUuid(w, "groupID", groupId);
        if (e.PinId is { } pinId) SwiftJson.WriteUuid(w, "pinID", pinId);
        if (e.Touched is { } touched) SwiftJson.WriteDate(w, "touched", touched);
        if (e.LetGo is { } letGo) SwiftJson.WriteDate(w, "letGo", letGo);
        if (e.Listed is { } listed) w.WriteBoolean("listed", listed);
        w.WriteBoolean("muted", e.Muted);
        if (e.TwinId is { } twinId) SwiftJson.WriteUuid(w, "twinID", twinId);
        if (e.Lifted is { } lifted) SwiftJson.WriteUuid(w, "lifted", lifted);
        w.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter w, TabGroup g)
    {
        w.WriteStartObject();
        SwiftJson.WriteUuid(w, "id", g.Id);
        w.WriteString("name", g.Name);
        w.WriteBoolean("collapsed", g.Collapsed);
        if (g.Colour is { } colour) w.WriteNumber("colour", colour);
        w.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter w, Split s)
    {
        w.WriteStartObject();
        w.WriteStartArray("tabs");
        foreach (var tab in s.Tabs) w.WriteNumberValue(tab);
        w.WriteEndArray();
        w.WriteString("axis", s.Axis == SplitAxis.Vertical ? "vertical" : "horizontal");
        w.WriteStartArray("sizes");
        foreach (var size in s.Sizes) w.WriteNumberValue(size);
        w.WriteEndArray();
        if (s.Focused is { } focused) w.WriteNumber("focused", focused);
        w.WriteEndObject();
    }

    /// Null when the file won't decode: its tabs and the tab in front are the parts that have to be there.
    public static Shape? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tabs", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            if (!root.TryGetProperty("active", out var front) || SwiftJson.Int(front) is not { } active) return null;
            var tabs = new List<Entry>();
            foreach (var item in list.EnumerateArray())
                if (ReadEntry(item) is { } entry) tabs.Add(entry); else return null;
            var groups = SwiftJson.Has(root, "groups", out var g) ? All(g, ReadGroup) : null;
            var splits = SwiftJson.Has(root, "splits", out var s) ? All(s, ReadSplit) ?? [] : [];
            return new Shape(tabs, active) { Groups = groups, Splits = splits };
        }
        catch (JsonException) { return null; }
    }

    /// Every one of them, or none: one that doesn't read spoils the list, as a failed try? in Swift would.
    private static List<T>? All<T>(JsonElement list, Func<JsonElement, T?> read) where T : class
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var all = new List<T>();
        foreach (var item in list.EnumerateArray())
            if (read(item) is { } one) all.Add(one); else return null;
        return all;
    }

    /// The address and title have to be there, and a pin, name or home that is there has to be words. Everything
    /// else that doesn't read is taken as not there.
    private static Entry? ReadEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("url", out var u) || SwiftJson.Text(u) is not { } url || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title) return null;
        if (!Optional(e, "pin", out var pin) || !Optional(e, "name", out var name) || !Optional(e, "home", out var home)) return null;
        return new Entry(url, title)
        {
            Pin = pin, Name = name, Home = home,
            GroupId = Lenient(e, "groupID", SwiftJson.Uuid),
            PinId = Lenient(e, "pinID", SwiftJson.Uuid),
            Touched = Lenient(e, "touched", SwiftJson.Date),
            LetGo = Lenient(e, "letGo", SwiftJson.Date),
            Listed = Lenient(e, "listed", SwiftJson.Bool),
            Muted = Lenient(e, "muted", SwiftJson.Bool) ?? false,
            TwinId = Lenient(e, "twinID", SwiftJson.Uuid),
            Lifted = Lenient(e, "lifted", SwiftJson.Uuid),
        };
    }

    /// A group saved without its folded state is an open one.
    private static TabGroup? ReadGroup(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id
            || !e.TryGetProperty("name", out var n) || SwiftJson.Text(n) is not { } name) return null;
        return new TabGroup(id, name, Lenient(e, "collapsed", SwiftJson.Bool) ?? false) { Colour = Lenient(e, "colour", SwiftJson.Int) };
    }

    /// An axis this version doesn't know is drawn side by side, and the first shape Split View was written in on
    /// main (a left, a right and a fraction) still reads.
    private static Split? ReadSplit(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        IReadOnlyList<int> tabs;
        IReadOnlyList<double> sizes;
        if (e.TryGetProperty("tabs", out var t) && Numbers(t, SwiftJson.Int) is { } listed)
        {
            tabs = listed;
            sizes = (e.TryGetProperty("sizes", out var s) ? Numbers(s, SwiftJson.Number) : null) ?? Even(listed.Count);
        }
        else
        {
            if (!e.TryGetProperty("left", out var l) || SwiftJson.Int(l) is not { } left || !e.TryGetProperty("right", out var r) || SwiftJson.Int(r) is not { } right) return null;
            var fraction = (e.TryGetProperty("fraction", out var f) ? SwiftJson.Number(f) : null) ?? 0.5;
            tabs = [left, right];
            sizes = [fraction, 1 - fraction];
        }
        var axis = Lenient(e, "axis", SwiftJson.Text) == "vertical" ? SplitAxis.Vertical : SplitAxis.Horizontal;
        return new Split(tabs, sizes) { Axis = axis, Focused = Lenient(e, "focused", SwiftJson.Int) };
    }

    private static List<T>? Numbers<T>(JsonElement list, Func<JsonElement, T?> read) where T : struct
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var all = new List<T>();
        foreach (var item in list.EnumerateArray())
            if (read(item) is { } one) all.Add(one); else return null;
        return all;
    }

    private static List<double> Even(int count) => Enumerable.Repeat(1 / (double)Math.Max(1, count), count).ToList();

    /// decodeIfPresent: missing or null is fine, there but not words is not.
    private static bool Optional(JsonElement e, string key, out string? value)
    {
        value = null;
        if (!SwiftJson.Has(e, key, out var v)) return true;
        value = SwiftJson.Text(v);
        return value is not null;
    }

    private static T? Lenient<T>(JsonElement e, string key, Func<JsonElement, T?> read) where T : struct => SwiftJson.Has(e, key, out var v) ? read(v) : null;

    private static string? Lenient(JsonElement e, string key, Func<JsonElement, string?> read) => SwiftJson.Has(e, key, out var v) ? read(v) : null;
}
