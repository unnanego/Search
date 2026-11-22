using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Search.Core;

// Zen's sidebar, read (ImportZen.swift): its spaces (Zen calls them workspaces), what is pinned in each, its folders,
// and the Essentials above them all. They come in the way Arc's do (ImportArc.cs, ImportTake.Arc): each space a space
// here, its pins that space's, the Essentials the pins every space shares, folders kept as folders where pinned rows
// can hold them.
//
// Zen has kept them three ways over its life, and each is read, newest first:
//  - Since its window sync (Zen 1.18, December 2025): "zen-sessions.jsonlz4" in the profile, one object of spaces,
//    tabs, folders and groups, with copies in zen-sessions-backup for when the file is torn.
//  - Before that: the tables zen_workspaces and zen_pins in places.sqlite, a pin a row, a folder a row marked is_group.
//  - Before those: the session store's own window, its pinned tabs marked with their workspace, and the spaces in
//    "zen-workspaces.json".
//
// Read as damaged files are: through the checked read (a regular file, no link followed, inside the profile), capped
// before and after unpacking, a folder no deeper than 64, 5000 pins in all, only http and https, a password dropped.
public static class Zen
{
    private const int MostPins = 5000;

    /// Zen's spaces and pins for one profile, or every one for null; null when none of them has a space to bring.
    public static ArcSidebar? Sidebar(GeckoSource source, string? profile)
    {
        var spaces = new List<ArcSpace>();
        var essentials = new List<ArcItem>();
        foreach (var folder in source.Folders(profile))
        {
            if (InProfile(folder) is not { } found) continue;
            spaces.AddRange(found.Spaces);
            foreach (var item in found.Favorites) if (!essentials.Any(e => e.Url == item.Url)) essentials.Add(item);
        }
        return spaces.Count == 0 ? null : Made(essentials, spaces);
    }

    private static ArcSidebar Made(List<ArcItem> essentials, IReadOnlyList<ArcSpace> spaces) => new(essentials, new Dictionary<string, IReadOnlyList<ArcItem>> { ["Default"] = essentials }, spaces);

    /// One profile's, the newest way Zen has kept them first.
    public static ArcSidebar? InProfile(string folder)
    {
        // Since window sync: the file, else its newest good copy.
        var backups = Path.Combine(folder, "zen-sessions-backup");
        string[] dated = [];
        try { if (ImportRead.IsPlainFolder(backups)) dated = Directory.GetFiles(backups, "zen-sessions-*.jsonlz4").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(5).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var file in new[] { Path.Combine(folder, "zen-sessions.jsonlz4"), Path.Combine(backups, "clean.jsonlz4"), Path.Combine(backups, "recovery.baklz4") }.Concat(dated))
        {
            using var top = MozLz4.Json(file, folder);
            if (top is not null && Parse(top.RootElement) is { Spaces.Count: > 0 } found) return found;
        }
        // Before it: the tables in places.sqlite.
        if (FromPlaces(Path.Combine(folder, "places.sqlite"), folder) is { Spaces.Count: > 0 } placed) return placed;
        // Before those: the session store's window, and zen-workspaces.json.
        using var store = MozillaSession.Store(folder);
        if (store is null) return null;
        var window = ImportSource.Objects(ImportSource.Get(store.RootElement, "windows")).FirstOrDefault(MozillaSession.Normal);
        if (window.ValueKind != JsonValueKind.Object) return null;
        if (ImportSource.Items(ImportSource.Get(window, "spaces")).Any()) return Parse(window) is { Spaces.Count: > 0 } kept ? kept : null;
        using var old = ImportSource.Json(ImportRead.TryData(Path.Combine(folder, "zen-workspaces.json"), folder));
        return Parse(window, old is null ? null : ImportSource.Get(old.RootElement, "workspaces")) is { Spaces.Count: > 0 } oldest ? oldest : null;
    }

    // The session file's shape.

    private sealed record Folder(string? Name, string? Parent, bool Split, bool Live);

    /// Zen's sidebar object, or a session window, which has the same fields: spaces, tabs (each pinned tab with the
    /// space it is in), and folders (a folder with the one it is in). `spaces`, for the oldest files, from elsewhere.
    public static ArcSidebar? Parse(JsonElement top, JsonElement? spaces = null)
    {
        var order = new List<string>();
        var made = new Dictionary<string, ArcSpace>();
        foreach (var raw in ImportSource.Objects(spaces ?? ImportSource.Get(top, "spaces")).Take(200))
        {
            if (ImportSource.Text(raw, "uuid") is not { } id || made.ContainsKey(id)) continue;
            order.Add(id);
            var theme = ImportSource.Get(raw, "theme");
            made[id] = Space(ImportSource.Text(raw, "name"), ImportSource.Text(raw, "icon"), ImportSource.Get(theme, "gradientColors"), SwiftJson.Number(ImportSource.Get(theme, "rotation")), SwiftJson.Number(ImportSource.Get(theme, "texture")));
        }
        if (order.Count == 0) return null;
        var first = order[0];

        var folders = new Dictionary<string, Folder>();
        foreach (var raw in ImportSource.Objects(ImportSource.Get(top, "folders")))
            if (ImportSource.Text(raw, "id") is { } id) folders[id] = new Folder(ImportSource.Text(raw, "name"), ImportSource.Text(raw, "parentId"), ImportSource.Flag(raw, "splitViewGroup"), ImportSource.Flag(raw, "isLiveFolder"));
        var names = folders.ToDictionary(f => f.Key, f => f.Value.Name);

        var tree = new Tree();
        var essentials = new List<ArcItem>();
        foreach (var tab in ImportSource.Objects(ImportSource.Get(top, "tabs")))
        {
            var essential = Truthy(ImportSource.Get(tab, "zenEssential"));
            if (!(ImportSource.Flag(tab, "pinned") || essential) || Truthy(ImportSource.Get(tab, "zenIsGlance")) || Truthy(ImportSource.Get(tab, "zenIsEmpty"))) continue;
            if (ImportSource.Text(tab, "zenLiveFolderItemId") is { Length: > 0 } || PinnedItem(tab) is not { } item) continue;
            if (essential)
            {
                if (!essentials.Any(e => e.Url == item.Url) && tree.Count < MostPins) { essentials.Add(item); tree.Count++; }
                continue;
            }
            var workspace = ImportSource.Text(tab, "zenWorkspace") ?? ImportSource.Text(ImportSource.Get(tab, "attributes"), "zen-workspace-id");
            var space = workspace is not null && made.ContainsKey(workspace) ? workspace : first;
            // The folder it is in, and the ones around that: a split view inside a folder is the folder's, a live
            // folder isn't brought.
            var chain = new List<string>();
            var at = ImportSource.Text(tab, "groupId");
            var live = false;
            while (at is not null && folders.TryGetValue(at, out var folder) && chain.Count < 64 && !chain.Contains(at))
            {
                if (folder.Live) live = true;
                if (!folder.Split) chain.Add(at);
                at = folder.Parent;
            }
            if (!live) tree.Place(item, chain, space, names);
        }
        return Made(essentials, order.Select(id => made[id] with { Pinned = tree.Nodes(id) }).ToList());
    }

    /// Pins in the order they come, each folder where its first page is, nested as the browser nests them.
    private sealed class Tree
    {
        private readonly Dictionary<string, List<object>> _roots = [], _children = [];
        private readonly Dictionary<string, string> _names = [];
        private readonly HashSet<string> _placed = [];
        public int Count;

        /// `chain` runs from the folder the page is in out to the top one. An entry is an ArcItem or a folder's id.
        public void Place(ArcItem item, List<string> chain, string space, IReadOnlyDictionary<string, string?> names)
        {
            if (Count >= MostPins) return;
            Count++;
            object child = item;
            foreach (var id in chain)
            {
                (_children.TryGetValue(id, out var kids) ? kids : _children[id] = []).Add(child);
                if (names.GetValueOrDefault(id) is { } name) _names[id] = name;
                if (!_placed.Add(id)) return;
                child = id;
            }
            (_roots.TryGetValue(space, out var top) ? top : _roots[space] = []).Add(child);
        }

        public List<ArcNode> Nodes(string space) => Build(_roots.GetValueOrDefault(space) ?? [], 0);

        private List<ArcNode> Build(List<object> entries, int depth)
        {
            var nodes = new List<ArcNode>();
            if (depth >= 64) return nodes;
            foreach (var entry in entries)
            {
                if (entry is ArcItem item) { nodes.Add(new ArcNode.Page(item)); continue; }
                var id = (string)entry;
                if (Build(_children.GetValueOrDefault(id) ?? [], depth + 1) is { Count: > 0 } inside) nodes.Add(new ArcNode.Folder(ImportUrl.Named(_names.GetValueOrDefault(id)) ?? "Folder", inside));
            }
            return nodes;
        }
    }

    /// A pinned tab's page: the one it was pinned at, which Zen keeps apart from wherever the tab has since gone, else
    /// where it is now.
    private static ArcItem? PinnedItem(JsonElement tab)
    {
        var entries = ImportSource.Objects(ImportSource.Get(tab, "entries")).ToList();
        var index = SwiftJson.Int(ImportSource.Get(tab, "index")) is { } i ? Math.Clamp(i, 1, Math.Max(entries.Count, 1)) - 1 : entries.Count - 1;
        var current = index >= 0 && index < entries.Count ? entries[index] : default;
        var initial = ImportSource.Get(ImportSource.Get(tab, "_zenPinnedInitialState"), "entry");
        if ((ImportUrl.Page(ImportSource.Text(initial, "url")) ?? ImportUrl.Page(ImportSource.Text(current, "url"))) is not { } url) return null;
        var title = ImportUrl.Named(ImportSource.Text(tab, "zenStaticLabel")) ?? ImportUrl.Named(ImportSource.Text(initial, "title")) ?? ImportUrl.Named(ImportSource.Text(current, "title")) ?? Arc.Fallback(url);
        return new ArcItem(title, url);
    }

    /// Zen has written its flags as booleans and, earlier, as "true".
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString() == "true",
        JsonValueKind.Number => value.TryGetDouble(out var n) && n != 0,
        _ => false,
    };

    // A space's look.

    /// A space with Zen's name, its icon (an emoji, or one of Zen's own pictures by its address) and its theme's colours.
    public static ArcSpace Space(string? name, string? icon, JsonElement colours, double? rotation, double? texture)
    {
        var named = ImportUrl.Named(name) ?? "Space";
        string? emoji = null, picture = null;
        if (icon?.Trim() is { Length: > 0 } trimmed)
        {
            if (trimmed.EndsWith(".svg", StringComparison.Ordinal)) picture = Path.GetFileNameWithoutExtension(trimmed.Split('/')[^1]);
            else if (new StringInfo(trimmed).LengthInTextElements <= 8) emoji = trimmed;
        }
        // The primary dot first, as Zen leads with it.
        var dots = ImportSource.Objects(colours).ToList();
        var rgb = dots.Where(d => ImportSource.Flag(d, "isPrimary")).Concat(dots.Where(d => !ImportSource.Flag(d, "isPrimary")))
            .Select(d => Rgb(ImportSource.Get(d, "c"))).OfType<(double R, double G, double B)>().Take(SpaceGradient.Most).ToList();
        var space = new ArcSpace(named, emoji, Arc.Symbol(emoji, null, picture, named), "Default", []);
        if (rgb.Count > 0) space = space with { Colour = Nearest(rgb[0]) };
        if (rgb.Count >= 2) space = space with { Gradient = new SpaceGradient(rgb.Select(Hex).ToList(), rotation is { } r && double.IsFinite(r) ? r : SpaceGradient.DefaultAngle, (texture ?? 0) > 0) };
        return space;
    }

    /// A colour as Zen keeps a dot's: [r, g, b] out of 255, or a custom one as "#rrggbb" or "rgb(r, g, b)".
    public static (double R, double G, double B)? Rgb(JsonElement value)
    {
        var parts = new List<double>();
        if (value.ValueKind == JsonValueKind.Array) parts = value.EnumerateArray().Take(3).Select(SwiftJson.Number).OfType<double>().ToList();
        else if (SwiftJson.Text(value)?.Trim() is { } text)
        {
            if (text.StartsWith('#') && text.Length == 7 && int.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) parts = [n >> 16 & 255, n >> 8 & 255, n & 255];
            else
            {
                var number = new StringBuilder();
                foreach (var c in text + " ")
                {
                    if (char.IsAsciiDigit(c) || c == '.') { number.Append(c); continue; }
                    if (number.Length > 0 && parts.Count < 3 && double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var part)) parts.Add(part);
                    number.Clear();
                }
            }
        }
        if (parts.Count != 3 || parts.Any(p => !double.IsFinite(p) || p < 0 || p > 255)) return null;
        return (parts[0] / 255, parts[1] / 255, parts[2] / 255);
    }

    public static string Hex((double R, double G, double B) c) => $"#{(int)Math.Round(c.R * 255, MidpointRounding.AwayFromZero):X2}{(int)Math.Round(c.G * 255, MidpointRounding.AwayFromZero):X2}{(int)Math.Round(c.B * 255, MidpointRounding.AwayFromZero):X2}";

    /// The nearest of Spaces.Colours by hue, or none for a grey: blue, green, orange, red and violet there.
    public static int Nearest((double R, double G, double B) c)
    {
        double high = Math.Max(c.R, Math.Max(c.G, c.B)), low = Math.Min(c.R, Math.Min(c.G, c.B));
        if (high <= 0 || (high - low) / high < 0.2 || high - low < 0.08) return 0;
        var delta = high - low;
        var hue = high == c.R ? (c.G - c.B) / delta : high == c.G ? 2 + (c.B - c.R) / delta : 4 + (c.R - c.G) / delta;
        hue = hue * 60 % 360;
        if (hue < 0) hue += 360;
        double Distance(double a) { var d = Math.Abs(a - hue); return Math.Min(d, 360 - d); }
        return new (int Index, double Hue)[] { (1, 218), (2, 146), (3, 33), (4, 353), (5, 270) }.MinBy(h => Distance(h.Hue)).Index;
    }

    // places.sqlite, before window sync.

    /// zen_workspaces and zen_pins, as Zen 1.x kept them until December 2025. Columns are read by name: they came one
    /// release at a time.
    public static ArcSidebar? FromPlaces(string file, string inside)
    {
        if (!File.Exists(file)) return null;
        using var copy = Snapshot.Of(file, inside);
        using var db = copy?.Open();
        if (db is null) return null;
        List<Dictionary<string, object>> Rows(string sql)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var row in db.Rows(sql))
            {
                if (rows.Count >= 10000) break;
                var values = new Dictionary<string, object>();
                for (var column = 0; column < row.Count; column++)
                {
                    if (row.Name(column) is not { } name) continue;
                    switch (row.Type(column))
                    {
                        case Sqlite.Integer: values[name] = row.Long(column); break;
                        case Sqlite.Float: values[name] = row.Double(column); break;
                        case Sqlite.Text: values[name] = row.Text(column) ?? ""; break;
                    }
                }
                rows.Add(values);
            }
            return rows;
        }
        static string? Str(Dictionary<string, object> row, string key) => row.GetValueOrDefault(key) as string;
        static long Int(Dictionary<string, object> row, string key) => row.GetValueOrDefault(key) is long n ? n : 0;

        var order = new List<string>();
        var made = new Dictionary<string, ArcSpace>();
        foreach (var raw in Rows("SELECT * FROM zen_workspaces ORDER BY position ASC, created_at ASC").Take(200))
        {
            if (Str(raw, "uuid") is not { } id || made.ContainsKey(id)) continue;
            order.Add(id);
            using var colours = ImportSource.Json(Str(raw, "theme_colors") is { } json ? Encoding.UTF8.GetBytes(json) : null);
            made[id] = Space(Str(raw, "name"), Str(raw, "icon"), colours?.RootElement ?? default, raw.GetValueOrDefault("theme_rotation") is long r ? r : null, raw.GetValueOrDefault("theme_texture") is double t ? t : null);
        }
        if (order.Count == 0) return null;
        var first = order[0];

        var pins = Rows("SELECT * FROM zen_pins ORDER BY position ASC");
        var names = new Dictionary<string, string?>();
        var parents = new Dictionary<string, string>();
        foreach (var pin in pins.Where(p => Int(p, "is_group") != 0))
        {
            if (Str(pin, "uuid") is not { } id) continue;
            names[id] = Str(pin, "title");
            if (Str(pin, "folder_parent_uuid") is { } parent) parents[id] = parent;
        }
        var tree = new Tree();
        var essentials = new List<ArcItem>();
        foreach (var pin in pins.Where(p => Int(p, "is_group") == 0))
        {
            if (ImportUrl.Page(Str(pin, "url")) is not { } url) continue;
            var item = new ArcItem(ImportUrl.Named(Str(pin, "title")) ?? Arc.Fallback(url), url);
            if (Int(pin, "is_essential") != 0)
            {
                if (!essentials.Any(e => e.Url == url) && tree.Count < MostPins) { essentials.Add(item); tree.Count++; }
                continue;
            }
            var space = Str(pin, "workspace_uuid") is { } w && made.ContainsKey(w) ? w : first;
            var chain = new List<string>();
            var at = Str(pin, "folder_parent_uuid");
            while (at is not null && names.ContainsKey(at) && chain.Count < 64 && !chain.Contains(at)) { chain.Add(at); at = parents.GetValueOrDefault(at); }
            tree.Place(item, chain, space, names);
        }
        return Made(essentials, order.Select(id => made[id] with { Pinned = tree.Nodes(id) }).ToList());
    }
}
