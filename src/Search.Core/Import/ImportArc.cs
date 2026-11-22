using System.Text.Json;

namespace Search.Core;

// Arc's sidebar, read (ImportArc.swift): its spaces and what is pinned in each, and the favourites above them. Arc
// keeps them in one plain JSON file beside its profiles, StorableSidebar.json, the same on Windows as on the Mac (Arc
// for Windows is the same app): no key, nothing signed in. This only reads it and says what is there; ImportTake.Arc
// makes the spaces and pins.
//
// The file keeps everything in flat lists of an id followed by its object: spaces, and items (a tab, a folder
// ("list"), or a container of items) joined by parentID and childrenIds. A space names its containers the same way, a
// label followed by an id: the "pinned" one is what is taken; the "unpinned" one is Arc's Today, which lives a day and
// is left behind.
//
// Read as damaged files are: a folder is taken once and no deeper than 64, 5000 things in all, only http and https
// addresses, a password in one dropped, and a file larger than any real sidebar (32 MB) isn't read at all.

public sealed record ArcItem(string Title, Uri Url);

/// A pinned page, or a folder of them, in the order Arc keeps them.
public abstract record ArcNode
{
    public sealed record Page(ArcItem Item) : ArcNode;
    public sealed record Folder(string Title, IReadOnlyList<ArcNode> Items) : ArcNode;
}

/// `Symbol`: one of Spaces.Icons, from Arc.Symbol. `Profile`: the Chromium profile folder the space signs in with,
/// "Default" or another's ("Profile 1"). `Colour` and `Gradient`: for a browser that keeps one (Zen's space themes);
/// Arc's spaces come without.
public sealed record ArcSpace(string Name, string? Emoji, string Symbol, string Profile, IReadOnlyList<ArcNode> Pinned)
{
    public int Colour { get; init; }
    public SpaceGradient? Gradient { get; init; }
}

/// `Favorites`: the icons at the top, shared by every space of their profile; `FavoritesByProfile`: the same by the
/// profile they belong to, for giving each space only its own profile's.
public sealed record ArcSidebar(IReadOnlyList<ArcItem> Favorites, IReadOnlyDictionary<string, IReadOnlyList<ArcItem>> FavoritesByProfile, IReadOnlyList<ArcSpace> Spaces)
{
    public int PinnedCount => Spaces.Sum(s => Arc.Count(s.Pinned));
}

public static class Arc
{
    /// Arc's sidebar for one profile ("Default", "Profile 1"), or every one for null. Null when there is no file or
    /// it doesn't read.
    public static ArcSidebar? Sidebar(ChromiumSource source, string? profile)
    {
        if (source.SidebarFile is not { } file || Path.GetDirectoryName(file) is not { } folder) return null;
        return ImportRead.TryData(file, folder, 32L << 20) is { } data ? Parse(data, profile) : null;
    }

    /// The names Arc shows for its profiles, by folder ("Profile 1" → "Raycast"), from its Local State.
    public static IReadOnlyDictionary<string, string> ProfileNames(ChromiumSource source) => source.LocalState().Names;

    public static ArcSidebar? Parse(byte[] data, string? wanted)
    {
        using var doc = ImportSource.Json(data);
        if (doc is null) return null;
        var main = ImportSource.Objects(ImportSource.Get(ImportSource.Get(doc.RootElement, "sidebar"), "containers")).FirstOrDefault(c => c.TryGetProperty("spaces", out _));
        if (main.ValueKind != JsonValueKind.Object) return null;

        var items = new Dictionary<string, JsonElement>();
        foreach (var item in ImportSource.Objects(ImportSource.Get(main, "items")))
            if (ImportSource.Text(item, "id") is { } id) items[id] = item;
        bool InProfile(string name) => wanted is null || wanted == name;

        // What a container or folder holds, in order: each item once, no more than 64 folders down, 5000 things in all.
        var taken = new HashSet<string>();
        var total = 0;
        List<ArcNode> Nodes(string id, int depth)
        {
            var nodes = new List<ArcNode>();
            if (depth >= 64 || !items.TryGetValue(id, out var holder)) return nodes;
            foreach (var child in ImportSource.Items(ImportSource.Get(holder, "childrenIds")))
            {
                if (SwiftJson.Text(child) is not { } childId || total >= 5000 || !taken.Add(childId) || !items.TryGetValue(childId, out var item)) continue;
                var data = ImportSource.Get(item, "data");
                if (ImportSource.Get(data, "tab") is { ValueKind: JsonValueKind.Object } tab)
                {
                    if (ImportUrl.Page(ImportSource.Text(tab, "savedURL")) is not { } url) continue;
                    total++;
                    nodes.Add(new ArcNode.Page(new ArcItem(ImportUrl.Named(ImportSource.Text(item, "title")) ?? ImportUrl.Named(ImportSource.Text(tab, "savedTitle")) ?? Fallback(url), url)));
                }
                else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out _) && Nodes(childId, depth + 1) is { Count: > 0 } inside)
                    nodes.Add(new ArcNode.Folder(ImportUrl.Named(ImportSource.Text(item, "title")) ?? "Folder", inside));
            }
            return nodes;
        }

        var spaces = new List<ArcSpace>();
        foreach (var space in ImportSource.Objects(ImportSource.Get(main, "spaces")))
        {
            var profile = ProfileName(ImportSource.Get(space, "profile"));
            if (!InProfile(profile)) continue;
            var name = ImportUrl.Named(ImportSource.Text(space, "title")) ?? "Space";
            var iconType = ImportSource.Get(ImportSource.Get(space, "customInfo"), "iconType");
            var emoji = ImportSource.Text(iconType, "emoji_v2");
            var pinned = Container("pinned", ImportSource.Get(space, "newContainerIDs")) ?? Container("pinned", ImportSource.Get(space, "containerIDs"));
            spaces.Add(new ArcSpace(name, emoji, Symbol(emoji, ImportSource.Text(iconType, "icon"), null, name), profile, pinned is null ? [] : Nodes(pinned, 0)));
        }

        // The favourites: a profile followed by its container's id.
        var favorites = new List<ArcItem>();
        var byProfile = new Dictionary<string, List<ArcItem>>();
        var tops = ImportSource.Items(ImportSource.Get(main, "topAppsContainerIDs")).ToList();
        for (var i = 0; i + 1 < tops.Count; i++)
        {
            if (tops[i].ValueKind == JsonValueKind.String) continue;
            var profile = ProfileName(tops[i]);
            if (SwiftJson.Text(tops[i + 1]) is not { } id || !InProfile(profile)) continue;
            foreach (var node in Nodes(id, 0).OfType<ArcNode.Page>())
            {
                favorites.Add(node.Item);
                (byProfile.TryGetValue(profile, out var list) ? list : byProfile[profile] = []).Add(node.Item);
            }
        }
        return new ArcSidebar(favorites, byProfile.ToDictionary(p => p.Key, p => (IReadOnlyList<ArcItem>)p.Value), spaces);
    }

    /// The id after `label` in a space's list of containers. Arc has written the label as a string and, lately, as an
    /// object keyed by it.
    private static string? Container(string label, JsonElement list)
    {
        var entries = ImportSource.Items(list).ToList();
        for (var i = 0; i + 1 < entries.Count; i++)
            if ((SwiftJson.Text(entries[i]) == label || ImportSource.Get(entries[i], label).ValueKind != JsonValueKind.Undefined) && SwiftJson.Text(entries[i + 1]) is { } id) return id;
        return null;
    }

    /// "Default", or the folder of the profile a space or favourites belong to: {"default": {}} or
    /// {"custom": {"_0": {"directoryBasename": …}}}.
    private static string ProfileName(JsonElement value)
    {
        var custom = ImportSource.Get(value, "custom");
        if (custom.ValueKind != JsonValueKind.Object) return "Default";
        var inner = ImportSource.Get(custom, "_0") is { ValueKind: JsonValueKind.Object } zero ? zero : custom;
        return ImportSource.Text(inner, "directoryBasename") is { Length: > 0 } folder ? folder : "Default";
    }

    internal static string Fallback(Uri url) => url.Host.Length > 0 ? url.Host : url.AbsoluteUri;

    public static int Count(IEnumerable<ArcNode> nodes) => nodes.Sum(n => n is ArcNode.Folder f ? Count(f.Items) : 1);

    // The space's picture.

    /// Arc's icon, or Zen's, or an emoji, as the nearest of Search's space pictures (Spaces.Icons); then the space's
    /// name; then a briefcase. The one table for both browsers.
    public static string Symbol(string? emoji, string? arcIcon, string? zenIcon, string name)
    {
        if (arcIcon is not null && ByArcIcon.TryGetValue(arcIcon.ToLowerInvariant(), out var arc)) return arc;
        if (zenIcon is not null && ByZenIcon.TryGetValue(zenIcon.ToLowerInvariant(), out var zen)) return zen;
        if (emoji is not null)
        {
            // The same emoji with or without the mark asking for its coloured form (U+FE0F), which Arc writes on some.
            var bare = emoji.Replace("️", "", StringComparison.Ordinal);
            foreach (var (symbol, emojis) in ByEmoji) if (emojis.Contains(bare)) return symbol;
        }
        // Whole words, or the start of one for the longer keys: "ai" is a word here, not the middle of "maison".
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant() + " ")
        {
            if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        foreach (var (symbol, keys) in ByWord)
            if (keys.Any(key => words.Any(w => w == key || (key.Length >= 4 && w.StartsWith(key, StringComparison.Ordinal))))) return symbol;
        return "briefcase";
    }

    /// Arc's built-in space icons, by the name its sidebar file gives them. Those not here fall back to the name.
    private static readonly Dictionary<string, string> ByArcIcon = new()
    {
        ["construction"] = "chevron.left.forwardslash.chevron.right", ["people"] = "heart", ["star"] = "sparkles", ["bulb"] = "lightbulb", ["leaf"] = "leaf", ["pizza"] = "cup.and.saucer",
    };

    /// Zen's space icons, by the name of the picture it draws (chrome://browser/skin/zen-icons/selectable/<name>.svg).
    private static readonly Dictionary<string, string> ByZenIcon = new (string Symbol, string[] Names)[]
    {
        ("airplane", ["airplane", "globe", "globe-1", "map", "location", "navigate", "planet"]),
        ("dumbbell", ["american-football", "baseball", "weight"]),
        ("cart", ["basket", "card", "wallet", "coins", "logo-usd", "shirt", "present"]),
        ("house", ["bed"]),
        ("book", ["book", "bookmark", "page", "logo-rss"]),
        ("briefcase", ["briefcase", "inbox", "mail", "stats-chart", "time", "call", "chat"]),
        ("paintpalette", ["brush", "palette", "shapes"]),
        ("camera", ["image"]),
        ("chevron.left.forwardslash.chevron.right", ["bug", "code", "logo-github", "extension-puzzle", "build", "construct"]),
        ("terminal", ["terminal"]),
        ("cup.and.saucer", ["cafe", "cutlery", "fast-food", "pizza", "egg", "ice-cream"]),
        ("gamecontroller", ["game-controller"]),
        ("heart", ["heart", "people"]),
        ("leaf", ["leaf", "water", "paw", "fish"]),
        ("lightbulb", ["lightning", "flame"]),
        ("brain.head.profile", ["flask", "nuclear"]),
        ("graduationcap", ["school"]),
        ("music.note", ["music", "volume-high"]),
        ("film", ["video", "ticket"]),
        ("sparkles", ["star", "star-1", "tada", "rocket"]),
        ("beach.umbrella", ["sun"]),
    }.SelectMany(g => g.Names.Select(n => (n, g.Symbol))).ToDictionary(p => p.n, p => p.Symbol);

    private static readonly (string Symbol, string[] Emojis)[] ByEmoji =
    [
        ("briefcase", ["💼", "👔", "📊", "📈", "🗂"]),
        ("building.2", ["🏢", "🏛", "🏙"]),
        ("house", ["🏠", "🏡", "🛋"]),
        ("desktopcomputer", ["🖥"]),
        ("laptopcomputer", ["💻"]),
        ("terminal", ["⌨", "🧑‍💻", "👨‍💻", "👩‍💻"]),
        ("chevron.left.forwardslash.chevron.right", ["🧑‍🔧", "🛠", "🔧", "⚙"]),
        ("sparkles", ["✨", "🤖", "🪄", "⭐", "🌟"]),
        ("brain.head.profile", ["🧠"]),
        ("lightbulb", ["💡"]),
        ("gamecontroller", ["🎮", "🕹", "👾"]),
        ("beach.umbrella", ["🏖", "🌴", "⛱", "🏝"]),
        ("cup.and.saucer", ["☕", "🍵", "🫖"]),
        ("music.note", ["🎵", "🎶", "🎧", "🎸", "🎹"]),
        ("film", ["🎬", "🍿", "📺", "🎥"]),
        ("paintpalette", ["🎨", "🖌", "🖍", "✏"]),
        ("camera", ["📷", "📸"]),
        ("book", ["📚", "📖", "📕", "📘", "📰"]),
        ("graduationcap", ["🎓", "🏫", "✍"]),
        ("cart", ["🛒", "🛍", "💸", "💰", "💳"]),
        ("airplane", ["✈", "🧳", "🌍", "🌎", "🌏", "🗺"]),
        ("dumbbell", ["🏋", "💪", "🏃", "⚽", "🏀"]),
        ("leaf", ["🌿", "🌱", "🍃", "🦋", "🌸", "🌳"]),
        ("heart", ["❤", "💖", "💕", "🥰", "😍"]),
    ];

    private static readonly (string Symbol, string[] Keys)[] ByWord =
    [
        ("briefcase", ["work", "job", "office", "boulot", "travail", "client"]),
        ("house", ["home", "perso", "personal", "maison", "family", "famille"]),
        ("chevron.left.forwardslash.chevron.right", ["code", "dev", "github", "eng"]),
        ("sparkles", ["ai", "gpt", "claude", "llm"]),
        ("graduationcap", ["school", "study", "studies", "course", "cours", "uni", "école"]),
        ("book", ["read", "lecture", "news", "research"]),
        ("paintpalette", ["design", "art", "creative", "figma"]),
        ("music.note", ["music", "musique"]),
        ("gamecontroller", ["game", "gaming", "jeu"]),
        ("cart", ["shop", "achat", "money", "finance"]),
        ("airplane", ["travel", "trip", "voyage"]),
        ("dumbbell", ["sport", "gym", "fitness"]),
        ("film", ["film", "movie", "video", "watch"]),
    ];
}
