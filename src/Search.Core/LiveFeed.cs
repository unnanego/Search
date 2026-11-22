using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Search.Core;

// A live folder's feed, read (LiveFolders.swift's Feed): RSS 2.0, RSS 1.0 (RDF) or Atom, turned into the folder's
// name and its latest posts, at most twelve, each once, by link. A post's link is read against the feed's address
// and kept only if it is a web one; a post with no title is called where it goes, as a bookmark with none reads; a
// title sent as HTML loses its tags, entities a feed escaped twice are read, and its spaces are made one. A feed that
// breaks off part way keeps the posts read before it. A web page is no feed, but the feed its <link rel="alternate">
// points to is found. What a person types is an address when it is a web one (a bare example.com/feed is https).
//
// The XML is read with no DTD (a DOCTYPE is passed over, never processed) and nothing fetched from outside it.
// Pure: LiveFetch fetches, LiveFolders keeps the folders fresh. A refetch's posts keep the ids they had, by link, so
// an unchanged feed changes nothing on disk.
public static partial class LiveFeed
{
    /// Arc keeps a live folder short; a column 260 points wide wants it so.
    public const int Most = 12;
    /// More than this from a feed's address and it isn't one.
    public const int MaxBytes = 2_000_000;
    /// A folder's name, at most; a post's title, at most.
    public const int MostName = 200, MostTitle = 300;
    /// A folder whose feed and site say no name.
    public const string DefaultName = "Live Folder";
    /// A live folder's heading's tooltip when its last fetch failed (else the feed's site).
    public const string CouldNotRefresh = "Couldn’t refresh";

    // New Live Folder…, as the Mac asks it.
    public const string AskTitle = "New Live Folder", AskDetail = "A feed’s address, or a site’s that has one. Its latest posts fill the folder, kept fresh every half hour.",
        AskPlaceholder = "https://example.com/feed.xml", AskConfirm = "Add", NotAddressTitle = "That isn’t a web address", NotAddressDetail = "A feed’s address starts with https:// or http://.",
        NoFeedTitle = "No feed found";

    /// What a feed said: its name, its address (the one found, when a page's was given), and its latest posts.
    public sealed record Read(string Title, string Feed, IReadOnlyList<FeedItem> Items)
    {
        public bool Equals(Read? other) => other is not null && Title == other.Title && Feed == other.Feed && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => HashCode.Combine(Title, Feed);
    }

    public enum Problem { Network, TooBig, NotAFeed }

    /// A fetch's outcome: the feed read, or why not.
    public sealed record Fetched(Read? Read, Problem? Problem)
    {
        public static Fetched Ok(Read read) => new(read, null);
        public static Fetched Failed(Problem problem) => new(null, problem);
    }

    /// Why no folder was made from `url` (the address asked for), in a sentence.
    public static string Said(Problem problem, string url)
    {
        var site = Host(url) ?? url;
        return problem switch
        {
            Problem.Network => $"{site} couldn’t be reached.",
            Problem.TooBig => $"What {site} sent is too large to be a feed.",
            _ => $"There’s no RSS or Atom feed at {url}, nor one that page points to.",
        };
    }

    /// What was typed, as an address: a bare `example.com/feed` is https; null for anything that isn't a web address.
    public static string? Address(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        var full = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Web(full) is { } uri ? uri.AbsoluteUri : null;
    }

    /// An https or http address with a host, or null.
    public static Uri? Web(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) ? Web(uri) : null;
    private static Uri? Web(Uri uri) => (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0 ? uri : null;

    /// An address's site, or null.
    public static string? Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    /// A live folder's heading's tooltip: its feed's site, or that its last fetch failed. Nothing for a plain folder.
    public static string Tip(PinFolder folder, bool failed) => folder.Feed is not { } feed ? "" : failed ? CouldNotRefresh : Host(feed) ?? feed;

    // Reading.

    /// RSS 2.0 (and 1.0) or Atom; null for anything else, a web page among them. `url` is the feed's address, which
    /// relative links are read against.
    public static Read? Parse(byte[] data, string url)
    {
        if (Web(url) is not { } address) return null;
        var reader = new FeedReader(address);
        try
        {
            using var xml = new XmlTextReader(new MemoryStream(data, false))
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, Namespaces = false, EntityHandling = EntityHandling.ExpandEntities, WhitespaceHandling = WhitespaceHandling.All,
            };
            reader.Read(xml);
        }
        catch (XmlException) { }
        if (!reader.IsFeed) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = reader.Items.Where(i => seen.Add(i.Link)).Take(Most).ToList();
        var title = Clean(reader.Title ?? "", html: false);
        return new Read(title.Length > 0 ? title : address.Host.Length > 0 ? address.Host : DefaultName, url, items);
    }

    /// A page's own feed: the first `<link rel="alternate">` of an RSS or Atom type in it, against the page's address.
    public static string? Discover(byte[] data, string page)
    {
        if (Web(page) is not { } at) return null;
        var bytes = data.Length > MaxBytes ? data[..MaxBytes] : data;
        string html;
        try { html = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { html = Encoding.Latin1.GetString(bytes); }
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attributes = Attributes(tag.Value);
            var rel = (attributes.GetValueOrDefault("rel") ?? "").ToLowerInvariant().Split(' ');
            var type = (attributes.GetValueOrDefault("type") ?? "").ToLowerInvariant();
            if (!rel.Contains("alternate") || type is not ("application/rss+xml" or "application/atom+xml") || attributes.GetValueOrDefault("href") is not { } href) continue;
            if (Decode(href).Trim() is { Length: > 0 } wanted && Uri.TryCreate(at, wanted, out var found) && Web(found) is { } web) return web.AbsoluteUri;
        }
        return null;
    }

    /// A tag's attributes, names lowercased, quoted either way or not.
    private static Dictionary<string, string> Attributes(string tag)
    {
        var made = new Dictionary<string, string>();
        foreach (Match m in AttributeExpression().Matches(tag))
            made[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
        return made;
    }

    /// A title as a line: tags taken out of one sent as HTML, entities a feed escaped twice read, and the spaces made one.
    public static string Clean(string text, bool html)
    {
        if (html) text = TagExpression().Replace(text, "");
        return string.Join(' ', Decode(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly Dictionary<string, string> _named = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ", ["#39"] = "'", ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–",
        ["rsquo"] = "’", ["lsquo"] = "‘", ["rdquo"] = "”", ["ldquo"] = "“",
    };

    /// The entities a title carries most: &amp; and friends, and numbers. One it doesn't know is left as it is.
    public static string Decode(string text) => !text.Contains('&') ? text : EntityExpression().Replace(text, m =>
    {
        var name = m.Groups[1].Value;
        if (_named.TryGetValue(name, out var known) || _named.TryGetValue(name.ToLowerInvariant(), out known)) return known;
        var number = name.StartsWith("#x", StringComparison.OrdinalIgnoreCase) ? (int.TryParse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1)
            : name.StartsWith('#') ? (int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : -1) : -1;
        return number is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(number) : m.Value;
    });

    /// At most `most` characters as a person counts them, never half of one.
    public static string Prefix(string text, int most)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= most ? text : info.SubstringByTextElements(0, most);
    }

    // The folder.

    /// A refetch's posts wearing the ids they already had, by link, so the column doesn't draw the same post anew and
    /// an unchanged feed changes nothing on disk.
    public static IReadOnlyList<FeedItem> KeepingIds(IReadOnlyList<FeedItem> fresh, IReadOnlyList<FeedItem> old)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in old) ids.TryAdd(item.Link, item.Id);
        return fresh.Select(item => ids.TryGetValue(item.Link, out var id) ? item with { Id = id } : item).ToList();
    }

    /// A fetch's outcome on a folder: read, its posts are the new ones (with the ids of those seen before) and no error;
    /// failed, the folder as it was and CouldNotRefresh. Only the posts change, never its name, place or feed.
    public static (PinFolder Folder, string? Error) Merged(PinFolder folder, Fetched result) =>
        result.Read is { } read ? (folder with { Items = KeepingIds(read.Items, folder.Items ?? []) }, null) : (folder, CouldNotRefresh);

    /// The same on a space's folders, to the folder as it is now (renamed or moved while it was fetched). Null when it
    /// is gone, or isn't live.
    public static (IReadOnlyList<PinFolder> Folders, string? Error)? Refreshed(IReadOnlyList<PinFolder> folders, Guid id, Fetched result)
    {
        if (folders.FirstOrDefault(f => f.Id == id) is not { IsLive: true } folder) return null;
        var (merged, error) = Merged(folder, result);
        return (folders.Select(f => f.Id == id ? merged : f).ToList(), error);
    }

    /// A live folder made from a feed read, inside `parent` if one may be made there, else at the top; named by the
    /// feed (at most 200 characters), with its posts.
    public static (IReadOnlyList<PinFolder> Folders, Guid Made) Made(IReadOnlyList<PinFolder> folders, Read read, Guid? parent = null)
    {
        var made = new PinFolder(Guid.NewGuid(), Prefix(read.Title, MostName)) { Parent = PinTree.CanMake(parent, folders) ? parent : null, Feed = read.Feed, Items = read.Items };
        return ([.. folders, made], made.Id);
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex LinkTag();
    [GeneratedRegex("""([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""")] private static partial Regex AttributeExpression();
    [GeneratedRegex("<[^>]*>")] private static partial Regex TagExpression();
    [GeneratedRegex("&(#[xX][0-9A-Fa-f]{1,6}|#[0-9]{1,7}|[A-Za-z]{2,8});")] private static partial Regex EntityExpression();

    /// RSS and Atom, element by element: the feed's title, and each post's title and link. Text is taken from the
    /// title, link and guid directly in a channel, feed, item or entry, so a post's source or media title is not.
    private sealed class FeedReader(Uri address)
    {
        public bool IsFeed { get; private set; }
        public string? Title { get; private set; }
        public List<FeedItem> Items { get; } = [];

        private readonly List<string> _path = [];
        private readonly StringBuilder _text = new();
        private bool _inPost, _postHtml, _guidIsLink = true, _titleHtml, _done;
        private string? _postTitle, _postLink, _postGuid;
        /// The element whose text is being gathered, and how deep it started.
        private (string Name, int Depth)? _gathering;

        /// Names as the Mac's parser saw them, prefixes off and lowercased: rdf:RDF is rdf.
        private static string Local(string name) => name[(name.LastIndexOf(':') + 1)..].ToLowerInvariant();

        public void Read(XmlTextReader xml)
        {
            while (!_done && xml.Read())
                switch (xml.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = Local(xml.Name);
                        var empty = xml.IsEmptyElement;
                        Start(name, xml);
                        if (empty) End(name);
                        break;
                    case XmlNodeType.EndElement: End(Local(xml.Name)); break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace: if (_gathering is not null) _text.Append(xml.Value); break;
                }
        }

        private void Gather(string name) { _gathering = (name, _path.Count); _text.Clear(); }

        private void Start(string name, XmlTextReader xml)
        {
            var parent = _path.Count > 0 ? _path[^1] : null;
            _path.Add(name);
            if (_path.Count == 1) IsFeed = name is "rss" or "feed" or "rdf";
            if (!IsFeed || _gathering is not null) return;
            switch (name)
            {
                case "item" or "entry":
                    _inPost = true;
                    _postTitle = _postLink = _postGuid = null; _postHtml = false; _guidIsLink = true;
                    break;
                case "title" when parent is "item" or "entry" or "channel" or "feed":
                    Gather(name);
                    _titleHtml = (xml.GetAttribute("type")?.ToLowerInvariant()) is "html" or "xhtml";
                    break;
                case "link" when _inPost && parent is "item" or "entry":
                    if (xml.GetAttribute("href") is { } href)
                    {
                        // Atom: the page is the alternate link, or one with no rel.
                        if ((xml.GetAttribute("rel")?.ToLowerInvariant() ?? "alternate") == "alternate") _postLink ??= href;
                    }
                    else Gather(name);
                    break;
                case "guid" when _inPost && parent == "item":
                    _guidIsLink = xml.GetAttribute("isPermaLink")?.ToLowerInvariant() != "false";
                    Gather(name);
                    break;
            }
        }

        private void End(string name)
        {
            try
            {
                if (_gathering is { } gathering && gathering.Depth == _path.Count && gathering.Name == name)
                {
                    _gathering = null;
                    var value = _text.ToString().Trim();
                    switch (name)
                    {
                        case "title": if (_inPost) { _postTitle = value; _postHtml = _titleHtml; } else Title ??= value; break;
                        case "link": if (_postLink is null && value.Length > 0) _postLink = value; break;
                        case "guid": _postGuid = value; break;
                    }
                    return;
                }
                if (name is not ("item" or "entry") || !_inPost) return;
                _inPost = false;
                if ((_postLink ?? (_guidIsLink ? _postGuid : null))?.Trim() is not { Length: > 0 } raw || !Uri.TryCreate(address, raw, out var uri) || Web(uri) is not { } link) return;
                var said = Clean(_postTitle ?? "", _postHtml);
                // No title: where it goes, as a bookmark with none reads.
                if (said.Length == 0) said = link.Host + (link.AbsolutePath == "/" ? "" : link.AbsolutePath);
                Items.Add(new FeedItem(Guid.NewGuid(), Prefix(said, MostTitle), link.AbsoluteUri));
                // Enough, with room for repeats: the rest of a long feed goes unread.
                if (Items.Count >= Most * 2) _done = true;
            }
            finally { if (_path.Count > 0) _path.RemoveAt(_path.Count - 1); }
        }
    }
}
