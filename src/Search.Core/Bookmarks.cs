using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Search.Core;

// Bookmarks: folders and sites, kept in a small file (Bookmarks.swift, the model and its file; BookmarksBar.swift for
// what the bar reads). bookmarks.json for Personal, bookmarks-<UUID>.json for each other profile, in the Mac's shape
// key for key (see SwiftJson): a list of {id, title, url?, children?}, a folder being the one without a url.
//
// Called from the UI thread, as the Swift is main-actor. Addresses are compared as .NET writes them (AbsoluteUri), on
// both sides, so "https://example.com" written by a Mac is the page Uri gives as "https://example.com/".

/// A site, or a folder (no address). An empty name on a site is a choice: on the bar it is its icon alone.
public sealed record Bookmark(Guid Id, string Title, string? Url = null, IReadOnlyList<Bookmark>? Children = null)
{
    public bool IsFolder => Url is null;
    /// A site whose name was emptied, as in Chrome and Safari. A folder always has a name.
    public bool Unnamed => !IsFolder && string.IsNullOrWhiteSpace(Title);
    /// What a list reads it as: its name, or for one without, its address as the address field shows it.
    public string Label => !Unnamed ? Title : Page is { } page ? Address.Pretty(page) : Url ?? "";
    public Uri? Page => Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var url) ? url : null;
    public string? Host => Page is { } page && Address.HasHost(page) ? page.Host.ToLowerInvariant() : null;
    /// The letter the bar and the menus wear for a site whose icon isn't kept yet.
    public string Letter => (Host is { Length: > 0 } host ? StringInfo.GetNextTextElement(host) : "•").ToUpperInvariant();
    /// Shown on hover over the bar's item.
    public string Hint => Url ?? Title;

    public static Bookmark Site(string title, Uri url) => new(Guid.NewGuid(), title.Length == 0 ? Address.Pretty(url) : title, url.AbsoluteUri);
    public static Bookmark Folder(string title, IReadOnlyList<Bookmark> children) => new(Guid.NewGuid(), title, null, children);

    /// The whole tree, as Swift's Equatable compares it.
    public bool Equals(Bookmark? other) => other is not null && Id == other.Id && Title == other.Title && Url == other.Url && Bookmarks.Same(Children, other.Children);
    public override int GetHashCode() => HashCode.Combine(Id, Title, Url);
}

public sealed class Bookmarks : Observable
{
    /// Which browser filled the empty top level, the first time.
    private const string TopKey = "bookmarks.top";
    private static readonly Guid PersonalProfile = new("00000000-0000-0000-0000-0000000000A1");

    /// A site as the address field looks for it, made once after each change: the field asks on every key.
    private sealed record Site(string Name, string Title, string Address, Uri Url, byte[] Findable, int Length);

    private readonly string _fileName;
    private readonly IDictionary<string, string> _settings;
    private IReadOnlyList<Bookmark> _roots = [];
    /// Every address kept, for Contains, asked on every redraw of the button.
    private HashSet<string> _kept = [];
    private List<Site>? _sites;
    private bool _retired;

    /// Where the two small notes the Mac keeps in its settings (the last folder picked, which browser filled the top
    /// level) live when the app doesn't hand over its own: memory only, for this run.
    public static IDictionary<string, string> SharedSettings { get; } = new Dictionary<string, string>();

    /// The list was saved: Sync takes it to the other machines.
    public static event Action<Bookmarks>? Saved;

    /// `settings` is where those two notes are kept, the same for every profile (the Mac's are its settings).
    public Bookmarks(string file = "bookmarks.json", IDictionary<string, string>? settings = null)
    {
        _fileName = file;
        _settings = settings ?? SharedSettings;
        Load();
    }

    /// bookmarks.json for Personal, one of its own for each other profile (Profiles.swift).
    public static string FileFor(Guid profile) => profile == PersonalProfile ? "bookmarks.json" : $"bookmarks-{SwiftJson.Uuid(profile)}.json";

    public IReadOnlyList<Bookmark> Roots
    {
        get => _roots;
        private set { _roots = value; _kept = Urls(value).Select(u => u.AbsoluteUri).ToHashSet(); _sites = null; Changed(); }
    }

    public bool IsEmpty => _roots.Count == 0;
    /// How many sites, the ones in folders included.
    public int Count => CountOf(_roots);

    public static int CountOf(IEnumerable<Bookmark> nodes) => nodes.Sum(n => n.IsFolder ? CountOf(n.Children ?? []) : 1);

    /// Every site in the list, in order, folders opened.
    public static IEnumerable<Uri> Urls(IEnumerable<Bookmark> nodes) =>
        nodes.SelectMany(n => n.IsFolder ? Urls(n.Children ?? []) : n.Page is { } url ? [url] : Enumerable.Empty<Uri>());

    /// Every folder in the tree with how deep it sits, for "move to" lists.
    public static IReadOnlyList<(Bookmark Node, int Depth)> Folders(IEnumerable<Bookmark> nodes, int depth = 0) =>
        nodes.Where(n => n.IsFolder).SelectMany(n => Folders(n.Children ?? [], depth + 1).Prepend((n, depth))).ToList();

    /// Where `node` can be moved to: every folder but itself and those inside it.
    public IReadOnlyList<(Bookmark Node, int Depth)> MoveTargets(Bookmark node) => Folders(_roots).Where(f => !Holds(f.Node.Id, node)).ToList();

    /// Every site and folder whose title or address holds all the words, with the folders it sits in: the rule
    /// chrome.bookmarks.search keeps.
    public IReadOnlyList<(Bookmark Node, IReadOnlyList<string> Path)> Matches(string text)
    {
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var found = new List<(Bookmark, IReadOnlyList<string>)>();
        void Walk(IEnumerable<Bookmark> nodes, List<string> path)
        {
            foreach (var node in nodes)
            {
                var hay = (node.Title + " " + (node.Url ?? "")).ToLowerInvariant();
                if (words.All(w => hay.Contains(w, StringComparison.Ordinal))) found.Add((node, path.ToList()));
                if (node.Children is { } kids) Walk(kids, [.. path, node.Title]);
            }
        }
        Walk(_roots, []);
        return found;
    }

    // In the address field.

    /// The sites for what is typed in the address field, the best `limit` first: every word, in any order, in the
    /// name or the address, allowing a typo. From two letters. Those found only through a typo come apart, in Slips.
    public (IReadOnlyList<Suggestion> Found, IReadOnlyList<Suggestion> Slips) Places(string typed, int limit)
    {
        var needle = typed.Trim(' ', '\t').ToLowerInvariant();
        var words = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (new StringInfo(needle).LengthInTextElements < 2 || words.Length == 0) return ([], []);
        var first = words[0];
        var bare = History.Stripped(needle);
        _sites ??= Sites(_roots).ToList();
        var bytes = words.Select(Encoding.UTF8.GetBytes).ToArray();
        // The address starting with what was typed first, then the name, then a word anywhere, then a typo; among
        // equals, the shorter address.
        var best = new List<(Site Site, int Score)>();
        foreach (var site in _sites)
        {
            if (Fuzzy.Match(bytes, site.Findable) is not { } found) continue;
            var score = found == 0 ? 0
                : site.Address.StartsWith(bare, StringComparison.Ordinal) ? 3
                : site.Title.StartsWith(needle, StringComparison.Ordinal) || site.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(first, StringComparison.Ordinal)) ? 2
                : 1;
            var at = best.FindIndex(b => score > b.Score || (score == b.Score && site.Length < b.Site.Length));
            if (at < 0) at = best.Count;
            if (at >= limit) continue;
            best.Insert(at, (site, score));
            if (best.Count > limit) best.RemoveAt(best.Count - 1);
        }
        static Suggestion Row(Site site) => new(Address.Editable(site.Url), site.Name, site.Url, SuggestionKind.Bookmark);
        return (best.Where(b => b.Score > 0).Select(b => Row(b.Site)).ToList(), best.Where(b => b.Score == 0).Select(b => Row(b.Site)).ToList());
    }

    /// Only places to go: a bookmarklet's javascript: is no address.
    private static IEnumerable<Site> Sites(IEnumerable<Bookmark> nodes) => nodes.SelectMany(node =>
    {
        if (node.IsFolder) return Sites(node.Children ?? []);
        if (node.Page is not { } url || url.Scheme is not ("http" or "https" or "file")) return [];
        var title = node.Unnamed ? "" : node.Title;
        var lowered = title.ToLowerInvariant();
        var address = History.Stripped(url.AbsoluteUri);
        return [new Site(title, lowered, address, url, Encoding.UTF8.GetBytes(lowered + " " + address), new StringInfo(address).LengthInTextElements)];
    });

    // Finding.

    /// The folders `id` sits in, outermost first; empty at the top level, null when it isn't here at all.
    public IReadOnlyList<Bookmark>? PathTo(Guid id)
    {
        List<Bookmark>? Walk(IEnumerable<Bookmark> nodes, List<Bookmark> above)
        {
            foreach (var node in nodes)
            {
                if (node.Id == id) return above;
                if (node.Children is { } kids && Walk(kids, [.. above, node]) is { } found) return found;
            }
            return null;
        }
        return Walk(_roots, []);
    }

    /// The folder `id` sits in, null at the top level.
    public Guid? Parent(Guid id) => PathTo(id) is { Count: > 0 } path ? path[^1].Id : null;

    /// The one kept for this address, wherever it is filed.
    public Bookmark? Find(Uri url) => url.IsAbsoluteUri && url.AbsoluteUri is var key ? First(b => b.Url is { } u && Key(u) == key) : null;

    public Bookmark? Find(Guid id) => First(b => b.Id == id);

    public bool Contains(Uri url) => url.IsAbsoluteUri && _kept.Contains(url.AbsoluteUri);

    private Bookmark? First(Func<Bookmark, bool> test)
    {
        Bookmark? Walk(IEnumerable<Bookmark> nodes)
        {
            foreach (var node in nodes)
            {
                if (test(node)) return node;
                if (node.Children is { } kids && Walk(kids) is { } found) return found;
            }
            return null;
        }
        return Walk(_roots);
    }

    /// The folder last picked on a bookmark's card: the next page bookmarked goes there too, as in Safari and Chrome.
    /// One for each profile. Null for the top level, and once that folder is gone.
    public Guid? LastFolder
    {
        get => _settings.TryGetValue(LastFolderKey, out var text) && SwiftJson.Uuid(text) is { } id && Find(id) is { IsFolder: true } ? id : null;
        set { if (value is { } id) _settings[LastFolderKey] = SwiftJson.Uuid(id); else _settings.Remove(LastFolderKey); }
    }

    /// Named after its file, which is the profile's.
    private string LastFolderKey => "bookmarks.lastFolder." + _fileName;

    /// Filed in `folder` (the top level for null) from its card, and the next page bookmarked goes there too.
    public void FileInto(Guid id, Guid? folder) { Move(id, folder); LastFolder = folder; }

    // Changing.

    /// The page, at the end of the list; nothing is asked first. Null when it is already kept.
    public Bookmark? Add(Uri url, string title)
    {
        if (Contains(url)) return null;
        var made = Bookmark.Site(title, url);
        Roots = [.. _roots, made];
        Save();
        return made;
    }

    public void Remove(Guid id) { Roots = Prune(id, _roots); Save(); }

    private static List<Bookmark> Prune(Guid id, IEnumerable<Bookmark> nodes) =>
        nodes.Where(n => n.Id != id).Select(n => n.Children is { } kids ? n with { Children = Prune(id, kids) } : n).ToList();

    /// What asking before removing says, for a folder with bookmarks in it (they all go with it, and there is no
    /// taking it back). Null for anything that goes at once.
    public static string? RemovalWarning(Bookmark node)
    {
        if (!node.IsFolder || node.Children is not { Count: > 0 } kids) return null;
        return CountOf(kids) switch { 0 => "The empty folders in it go too.", 1 => "The bookmark in it goes too.", var n => $"The {n} bookmarks in it go too." };
    }

    /// Takes a bookmark or a whole folder out of wherever it sits and puts it in another folder, or at the top level
    /// for null, at `index` among what is there, or at the end. The index is counted as the list reads before the
    /// move, so dropping a row just below itself leaves it where it was. Moving a folder into its own children is
    /// refused; onto itself is nothing to do.
    public void Move(Guid id, Guid? folder, int? index = null)
    {
        if (id == folder) return;
        if (index is { } at && Siblings(folder).ToList().FindIndex(n => n.Id == id) is var from and >= 0 && from < at) index = at - 1;
        var working = _roots.ToList();
        if (Detach(id, working) is not { } node) return;
        if (folder is { } target && Holds(target, node)) return;
        if (!Place(node, folder, index, working)) return;
        Roots = working;
        Save();
    }

    /// What a folder holds, or the top level for null.
    private IReadOnlyList<Bookmark> Siblings(Guid? folder) => folder is { } id ? Find(id)?.Children ?? [] : _roots;

    private static Bookmark? Detach(Guid id, List<Bookmark> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id) { var found = nodes[i]; nodes.RemoveAt(i); return found; }
            if (nodes[i].Children is not { } children) continue;
            var kids = children.ToList();
            if (Detach(id, kids) is { } inside) { nodes[i] = nodes[i] with { Children = kids }; return inside; }
        }
        return null;
    }

    /// `node` into the folder `id` (the top level for null) at `index`, clamped to what is there, or at the end.
    /// False when there is no such folder.
    private static bool Place(Bookmark node, Guid? id, int? index, List<Bookmark> nodes)
    {
        if (id is null) { nodes.Insert(Math.Clamp(index ?? nodes.Count, 0, nodes.Count), node); return true; }
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id && nodes[i].IsFolder)
            {
                var kids = nodes[i].Children?.ToList() ?? [];
                kids.Insert(Math.Clamp(index ?? kids.Count, 0, kids.Count), node);
                nodes[i] = nodes[i] with { Children = kids };
                return true;
            }
            if (nodes[i].Children is not { } children) continue;
            var inside = children.ToList();
            if (Place(node, id, index, inside)) { nodes[i] = nodes[i] with { Children = inside }; return true; }
        }
        return false;
    }

    /// `id` is `node` itself, or somewhere inside it.
    public static bool Holds(Guid id, Bookmark node) => node.Id == id || (node.Children ?? []).Any(k => Holds(id, k));

    /// Another browser's, kept apart in a folder of that browser's name unless there was nothing here yet. Bringing
    /// them in again adds only what is new (a page already in the same place is left, a folder of the same name is
    /// gone into) and never takes away what you have added or moved since.
    public (int Added, int Already) Take(IReadOnlyList<Bookmark> nodes, string name) { var (added, already, _) = TakeNoting(nodes, name); return (added, already); }

    /// The same, and every bookmark and folder it added, so they can be taken back out exactly (see Withdraw).
    public (int Added, int Already, IReadOnlyList<Guid> Ids) TakeNoting(IReadOnlyList<Bookmark> nodes, string name)
    {
        if (nodes.Count == 0) return (0, 0, []);
        int added = 0, already = 0;
        var ids = new List<Guid>();
        if (IsEmpty)
        {
            Roots = nodes.ToList();
            added = CountOf(nodes);
            ids = Ids(nodes).ToList();
            _settings[TopKey] = name;
        }
        else if (IntoTop(nodes, name))
        {
            var top = _roots.ToList();
            Merge(nodes, top, ref added, ref already, ids);
            Roots = top;
        }
        else
        {
            var top = _roots.ToList();
            var at = top.FindIndex(n => n.IsFolder && n.Title == name);
            var kids = at >= 0 ? top[at].Children?.ToList() ?? [] : [];
            Merge(nodes, kids, ref added, ref already, ids);
            if (at >= 0) top[at] = top[at] with { Children = kids };
            else { var folder = Bookmark.Folder(name, kids); ids.Add(folder.Id); top.Add(folder); }
            Roots = top;
        }
        Save();
        return (added, already, ids);
    }

    /// Whether this browser's bookmarks belong at the top level: it filled it the first time, or, from before that
    /// was noted, most of its pages are already there.
    private bool IntoTop(IReadOnlyList<Bookmark> nodes, string name)
    {
        if (_settings.TryGetValue(TopKey, out var top)) return top == name;
        var theirs = Urls(nodes).Select(u => u.AbsoluteUri).ToList();
        if (theirs.Count == 0) return false;
        var ours = Urls(_roots).Select(u => u.AbsoluteUri).ToHashSet();
        if (theirs.Count(ours.Contains) * 2 < theirs.Count) return false;
        _settings[TopKey] = name;
        return true;
    }

    /// `incoming` into `nodes`, level by level: a folder into the folder of the same name, a page only if the same
    /// address isn't already at that level.
    private static void Merge(IEnumerable<Bookmark> incoming, List<Bookmark> nodes, ref int added, ref int already, List<Guid> ids)
    {
        var folders = new Dictionary<string, int>();
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].IsFolder) folders.TryAdd(nodes[i].Title, i);
        var pages = nodes.Where(n => !n.IsFolder).Select(n => Key(n.Url!)).ToHashSet();
        foreach (var node in incoming)
        {
            if (node.IsFolder)
            {
                if (folders.TryGetValue(node.Title, out var at))
                {
                    var kids = nodes[at].Children?.ToList() ?? [];
                    Merge(node.Children ?? [], kids, ref added, ref already, ids);
                    nodes[at] = nodes[at] with { Children = kids };
                }
                else
                {
                    folders[node.Title] = nodes.Count;
                    nodes.Add(node);
                    added += CountOf([node]);
                    ids.AddRange(Ids([node]));
                }
            }
            else if (pages.Contains(Key(node.Url!))) already++;
            else
            {
                pages.Add(Key(node.Url!));
                nodes.Add(node);
                added++;
                ids.Add(node.Id);
            }
        }
    }

    /// Takes back out what an import added: every bookmark among `ids` wherever it now sits, then every folder among
    /// them left empty. A folder of theirs you have put something of your own in stays, with that in it. Returns how
    /// many bookmarks went.
    public int Withdraw(IEnumerable<Guid> ids)
    {
        var ours = ids.ToHashSet();
        var gone = 0;
        List<Bookmark> Strip(IEnumerable<Bookmark> nodes)
        {
            var kept = new List<Bookmark>();
            foreach (var node in nodes)
            {
                if (!node.IsFolder && ours.Contains(node.Id)) { gone++; continue; }
                if (node.Children is not { } kids) { kept.Add(node); continue; }
                var left = Strip(kids);
                if (ours.Contains(node.Id) && left.Count == 0) continue;
                kept.Add(node with { Children = left });
            }
            return kept;
        }
        Roots = Strip(_roots);
        Save();
        return gone;
    }

    /// Every id in a tree, folders and all.
    private static IEnumerable<Guid> Ids(IEnumerable<Bookmark> nodes) => nodes.SelectMany(n => Ids(n.Children ?? []).Prepend(n.Id));

    /// A page or a folder filed under `parent` at `index`, or at the top level for null or a folder that isn't there.
    /// What chrome.bookmarks.create does, and New Folder.
    public Bookmark Insert(Bookmark node, Guid? parent, int? index = null)
    {
        var nodes = _roots.ToList();
        if (!Place(node, parent, index, nodes)) Place(node, null, index, nodes);
        Roots = nodes;
        Save();
        return node;
    }

    /// A new title or address for one that is kept (a folder's address stays none). chrome.bookmarks.update, and Rename.
    public void Update(Guid id, string? title, string? url)
    {
        bool Walk(List<Bookmark> nodes)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Id == id)
                {
                    nodes[i] = nodes[i] with { Title = title ?? nodes[i].Title, Url = url is not null && !nodes[i].IsFolder ? url : nodes[i].Url };
                    return true;
                }
                if (nodes[i].Children is not { } children) continue;
                var kids = children.ToList();
                if (Walk(kids)) { nodes[i] = nodes[i] with { Children = kids }; return true; }
            }
            return false;
        }
        var working = _roots.ToList();
        if (!Walk(working)) return;
        Roots = working;
        Save();
    }

    /// The name typed on the card, trimmed; an empty one is kept as empty (the bar then shows the icon alone).
    public void Rename(Guid id, string typed)
    {
        var name = typed.Trim();
        if (name != Find(id)?.Title) Update(id, name, null);
    }

    /// An address changed by hand, read the way the address field reads what is typed. False, and nothing saved, for
    /// what that refuses (a bookmarklet, a word) or for a folder.
    public bool SetAddress(Guid id, string typed)
    {
        if (Find(id) is not { IsFolder: false } || Address.UrlFrom(typed) is not { } url) return false;
        Update(id, null, url.AbsoluteUri);
        return true;
    }

    /// The list as another machine left it (see Sync.swift).
    public void Adopt(IReadOnlyList<Bookmark> nodes)
    {
        if (Same(nodes, _roots)) return;
        Roots = nodes.ToList();
        Save();
    }

    /// Its profile deleted: its file removed, and nothing written again.
    public void Retire()
    {
        _retired = true;
        // Through the queue, so a write still on its way can't bring the file back after it.
        Disk.Write(FilePath, () => { try { System.IO.File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; });
    }

    // The bar and the dropdown.

    /// The bar's items: the top of the bookmarks, in order. A folder opens as a menu.
    public IReadOnlyList<Bookmark> Bar => _roots;

    /// The folders open as the dropdown opens: a top level that is one folder and nothing else, as bringing bookmarks
    /// in from another browser often leaves.
    public static IReadOnlySet<Guid> Opening(IReadOnlyList<Bookmark> roots) => roots is [{ IsFolder: true } only] ? new HashSet<Guid> { only.Id } : new HashSet<Guid>();

    /// A folder's own sites, not those of the folders in it, for Open All in New Tabs.
    public static IReadOnlyList<Uri> OpenAll(Bookmark folder) => (folder.Children ?? []).Where(k => !k.IsFolder).Select(k => k.Page).OfType<Uri>().ToList();

    // The file.

    private string FilePath => Store.File(_fileName);

    /// An address as the list compares it.
    private static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsoluteUri : url;

    internal static bool Same(IReadOnlyList<Bookmark>? a, IReadOnlyList<Bookmark>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.Count == b.Count && a.Zip(b).All(p => p.First.Equals(p.Second)));

    private void Load()
    {
        byte[] data;
        try { data = System.IO.File.ReadAllBytes(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (Decode(data) is { } list) Roots = list; else Store.Quarantine(FilePath);
    }

    private void Save()
    {
        if (_retired) return;
        var snapshot = _roots;
        // One after another, the newest last (see Disk).
        Disk.Write(FilePath, () => Encode(snapshot));
        Saved?.Invoke(this);
    }

    public static byte[] Encode(IEnumerable<Bookmark> nodes) => SwiftJson.Encode(w => WriteList(w, nodes));

    private static void WriteList(Utf8JsonWriter w, IEnumerable<Bookmark> nodes)
    {
        w.WriteStartArray();
        foreach (var node in nodes)
        {
            w.WriteStartObject();
            SwiftJson.WriteUuid(w, "id", node.Id);
            w.WriteString("title", node.Title);
            if (node.Url is { } url) w.WriteString("url", url);
            if (node.Children is { } kids) { w.WritePropertyName("children"); WriteList(w, kids); }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// Null when the file won't decode, as Swift's decoder sees it: every bookmark needs its id and title, and a url
    /// or children that are there have to be words and bookmarks.
    public static List<Bookmark>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 512 });
            return ReadList(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static List<Bookmark>? ReadList(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var nodes = new List<Bookmark>();
        foreach (var item in list.EnumerateArray())
            if (ReadNode(item) is { } node) nodes.Add(node); else return null;
        return nodes;
    }

    private static Bookmark? ReadNode(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title) return null;
        string? url = null;
        if (SwiftJson.Has(e, "url", out var u) && (url = SwiftJson.Text(u)) is null) return null;
        List<Bookmark>? children = null;
        if (SwiftJson.Has(e, "children", out var c) && (children = ReadList(c)) is null) return null;
        return new Bookmark(id, title, url, children);
    }
}
