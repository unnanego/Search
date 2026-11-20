using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Search.Core;

// Where you have been, so the field can finish the address for you (History.swift). Kept in one small file next to
// the settings, written a moment after a visit rather than on every keystroke, in the Mac's format (see SwiftJson).
//
// Called from the UI thread; the file is read on the thread pool and the pause before a write runs out there, so
// everything is behind one lock. Changes made while the file is still being read are shown at once and replayed
// over what the file had once it is in.

public enum SuggestionKind
{
    /// A page that is open right now.
    Open,
    /// Somewhere you have actually been.
    Visited,
    /// One of your bookmarks.
    Bookmark,
    /// One of the well-known addresses the field knows from the start.
    Known,
    /// Not a place at all: words, and an engine to ask.
    Search,
    /// Not a place either: something the app itself does. A command goes nowhere; its Url is only carried.
    Command,
}

/// `Key` is what you could type to get here, kept reversible for completion.
public sealed record Suggestion(string Key, string Title, Uri Url, SuggestionKind Kind)
{
    /// Set when this is a page you already have open somewhere.
    public Guid? Tab { get; init; }

    /// A command's row can read the same as the search for the same word, and two rows can't share a name.
    public string Id => Kind switch
    {
        SuggestionKind.Command => "command " + Key,
        SuggestionKind.Visited or SuggestionKind.Bookmark => History.Identity(Url),
        _ => Key,
    };
}

/// Somewhere you have been, for the window that lists them. `Address` is reversible, for display; `Key` is the
/// stable URL identity.
public sealed record Trace(string Key, string Address, string Title, Uri Url, DateTimeOffset Last, int Count);

public sealed class History
{
    /// Visits counted at most: far more than anyone makes, and room to add.
    public const int MostVisits = 1_000_000_000;
    /// Places kept: in the file, and in memory once a session has run long enough to be cut (see Prune).
    public const int Cap = 2_000;
    /// How far memory may run past Cap before it is cut back to it, and how many of the newest places the cut keeps
    /// whatever they score.
    public const int Slack = 250;
    /// Frecency's month, in seconds.
    private const double Fading = 30.0 * 86_400;
    /// A name the app merely knows: under one fresh visit.
    private static readonly double KnownWeight = Math.Log(1.5);
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);

    private struct Visit
    {
        public string Url, Key, Title;
        public int Count;
        public DateTimeOffset Last;
    }

    /// Derived once when a visit enters memory: suggestions run on every key, so neither URL parsing nor address
    /// formatting belongs in that loop.
    private sealed class PageText
    {
        public required string Address, Searchable, Host;
        public required byte[] SearchableBytes, FindableBytes;
        public required bool Homepage;
        /// Prefixes its visits count for: host, host/team, host/team/api.
        public required string[] Trail;
        public double Weight;
    }

    private sealed class LoadedHistory
    {
        public Dictionary<string, Visit> Visits = [];
        public Dictionary<string, PageText> PageText = [];
        public Dictionary<string, double> Weights = [];
        public HashSet<string> VisitedHosts = [];
        public bool Cleaned, Corrupt;
    }

    private readonly Lock _lock = new();
    private readonly string _file;
    private readonly DateTimeOffset _epoch = DateTimeOffset.UtcNow;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, Visit> _visits = [];
    private Dictionary<string, PageText> _pageText = [];
    /// Frecency under each address prefix, as of `_epoch`, faded on reading.
    private Dictionary<string, double> _weights = [];
    private HashSet<string> _visitedHosts = [];
    /// The last eight places; worked out again only once the history has changed.
    private List<Trace>? _recentCache;
    /// Something in memory the file doesn't have yet.
    private bool _owed;
    /// A write waiting out the pause.
    private bool _pausing;
    private bool _changed;
    private Task<LoadedHistory>? _loading;
    private List<Action<History>> _duringLoad = [];

    public event Action? Changed;

    /// Done once the file is in memory (or found missing or unreadable).
    public Task Loaded => _loaded.Task;

    /// history.json for Personal, one of its own for each other profile.
    public History(string file = "history.json")
    {
        _file = Store.File(file);
        // Applied where it was asked for (the UI thread, when there is one), as Swift's main queue did.
        var context = SynchronizationContext.Current;
        var job = _loading = Task.Run(() => Read(_file, _epoch));
        job.ContinueWith(_ => { if (context is null) Finish(job); else context.Post(_ => Finish(job), null); }, TaskScheduler.Default);
    }

    // URLs.

    /// URL identity ignores fragments and a name and password, normalizes the scheme and host, and keeps the path
    /// and query as they are. An absent path and a lone slash mean the same home page (Uri gives every web address
    /// a "/").
    public static string Identity(Uri url) =>
        !url.IsAbsoluteUri ? url.OriginalString
        : Address.HasHost(url) ? url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
        : url.AbsoluteUri[..^url.Fragment.Length];

    /// A page as it is kept: without a name and password written into its address. They were never part of which
    /// page it is, and the history file is no place for a password. Null if they can't be taken off.
    public static Uri? Kept(Uri url)
    {
        if (!url.IsAbsoluteUri) return null;
        if (url.UserInfo.Length == 0) return url;
        return Uri.TryCreate(url.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped), UriKind.Absolute, out var kept) ? kept : null;
    }

    private static bool IsWeb(Uri url) => url.IsAbsoluteUri && url.Scheme is "http" or "https";

    private static Uri? Homepage(Uri url) =>
        Address.HasHost(url) && Uri.TryCreate(url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/", UriKind.Absolute, out var home) ? home : null;

    private static Uri? Parse(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    private static bool IsHomepath(Uri url) => url.AbsolutePath is "" or "/";

    public static string Stripped(string address)
    {
        var text = address.Trim(' ', '\t').ToLowerInvariant();
        foreach (var scheme in (string[])["https://", "http://"])
            if (text.StartsWith(scheme, StringComparison.Ordinal)) text = text[scheme.Length..];
        return text.StartsWith("www.", StringComparison.Ordinal) ? text[4..] : text;
    }

    private static PageText Text(Uri url, string title = "")
    {
        var shown = url.Fragment.Length == 0 ? url : Parse(url.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped)) ?? url;
        var address = Address.Editable(shown);
        var searchable = Stripped(address);
        var host = Address.HasHost(shown) ? shown.Host.ToLowerInvariant() : "";
        var findable = title.ToLowerInvariant() + " " + searchable;
        return new PageText
        {
            Address = address, Searchable = searchable, Host = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host,
            SearchableBytes = Encoding.UTF8.GetBytes(searchable), FindableBytes = Encoding.UTF8.GetBytes(findable),
            Homepage = IsHomepath(shown), Trail = Trail(searchable),
        };
    }

    private static string[] Trail(string searchable)
    {
        var own = searchable.EndsWith('/') ? searchable[..^1] : searchable;
        var cut = own.IndexOfAny(['?', '#']);
        var path = cut < 0 ? own : own[..cut];
        var trail = new List<string>();
        for (var i = 0; i < path.Length; i++) if (path[i] == '/') trail.Add(path[..i]);
        if (path.Length < own.Length) trail.Add(path);
        trail.Add(own);
        return [.. trail];
    }

    // Memory.

    private double Weight(Visit visit) => visit.Count * Math.Exp((visit.Last - _epoch).TotalSeconds / Fading);

    private void Remember(Visit visit, Uri? url = null)
    {
        if ((url ?? Parse(visit.Url)) is not { } page) return;
        var text = Text(page, visit.Title);
        text.Weight = Weight(visit);
        if (_pageText.TryGetValue(visit.Key, out var old)) Credit(old, -old.Weight);
        Credit(text, text.Weight);
        _pageText[visit.Key] = text;
        if (text.Host.Length > 0) _visitedHosts.Add(text.Host);
    }

    private void Credit(PageText text, double weight)
    {
        foreach (var prefix in text.Trail)
        {
            var had = _weights.GetValueOrDefault(prefix);
            var now = had + weight;
            // Taking the last page's share back out leaves nothing, or a crumb of rounding, and either way the
            // prefix is no longer anyone's: left in, every address ever visited keeps a key here.
            if (now > had * 1e-9) _weights[prefix] = now; else _weights.Remove(prefix);
        }
    }

    private void ForgetText(string key)
    {
        if (!_pageText.Remove(key, out var old)) return;
        Credit(old, -old.Weight);
        if (old.Host.Length > 0 && !_pageText.Values.Any(t => t.Host == old.Host)) _visitedHosts.Remove(old.Host);
    }

    private void Set(string key, Visit visit) { _visits[key] = visit; Touch(); }

    private void Touch() { _recentCache = null; _changed = true; }

    /// Raised outside the lock, once per call that changed something.
    private void Publish()
    {
        bool changed;
        lock (_lock) { changed = _changed; _changed = false; }
        if (changed) Changed?.Invoke();
    }

    private void Later(Action<History> change) { if (_loading is not null) _duringLoad.Add(change); }

    // Writing.

    public void Record(Uri url, string title)
    {
        lock (_lock) { Later(h => h.RecordNow(url, title)); RecordNow(url, title); }
        Publish();
    }

    private void RecordNow(Uri url, string title)
    {
        if (!IsWeb(url) || Kept(url) is not { } kept) return;
        var key = Identity(kept);
        if (key.Length == 0) return;
        var text = Text(kept);
        var now = DateTimeOffset.UtcNow;

        // Reading a deep page is also, in the way that matters here, another visit to the site. Without this, typing
        // three letters offers the article you happened to open last week rather than the front page, and nobody
        // types a domain meaning to land halfway down it.
        if (!text.Homepage && Homepage(kept) is { } homeUrl)
        {
            var root = Identity(homeUrl);
            var home = _visits.TryGetValue(root, out var had) ? had : new Visit { Url = homeUrl.AbsoluteUri, Key = root, Title = "", Count = 0 };
            home.Count = Math.Min(MostVisits, home.Count + 1);
            home.Last = now;
            Set(root, home);
            Remember(home, homeUrl);
        }

        if (_visits.TryGetValue(key, out var seen))
        {
            seen.Count = Math.Min(MostVisits, seen.Count + 1);
            seen.Last = now;
            seen.Url = kept.AbsoluteUri;
            if (title.Length > 0) seen.Title = title;
        }
        else seen = new Visit { Url = kept.AbsoluteUri, Key = key, Title = title, Count = 1, Last = now };
        Set(key, seen);
        Remember(seen, kept);
        Prune();
        Save();
    }

    /// Memory is cut back to Cap places once it is Slack past it: otherwise a session that never restarts holds, and
    /// the field scans on every key, every page it has ever seen. The newest Slack places stay whatever they score:
    /// a page opened a minute ago scores about 1, under any older page visited twice, and it must not leave the
    /// History window and the field while it is still the page you are on. The rest of the cap goes to the highest
    /// score, the order the file keeps. What goes takes its text and its share of every prefix with it.
    private void Prune()
    {
        if (_visits.Count <= Cap + Slack) return;
        var now = DateTimeOffset.UtcNow;
        var kept = _visits.Values.OrderByDescending(v => v.Last).Take(Slack).Select(v => v.Key).ToHashSet();
        kept.UnionWith(_visits.Values.Where(v => !kept.Contains(v.Key)).OrderByDescending(v => Score(v, now)).Take(Cap - Slack).Select(v => v.Key));
        foreach (var key in _visits.Keys.Where(k => !kept.Contains(k)).ToList())
        {
            _visits.Remove(key);
            if (_pageText.Remove(key, out var old)) Credit(old, -old.Weight);
        }
        // Once, not as each page goes: asking whether another page still has the host, for each of a few hundred,
        // would scan the rest each time.
        _visitedHosts = _pageText.Values.Select(t => t.Host).Where(h => h.Length > 0).ToHashSet();
        Touch();
    }

    /// Somewhere another browser has been. Counted as it was counted there, so a site visited daily for a year
    /// outranks one seen once: the day you switch, the field already knows you. Settle() after a batch.
    public void Take(Uri url, string title, int count, DateTimeOffset last)
    {
        lock (_lock) { Later(h => h.TakeNow(url, title, count, last)); TakeNow(url, title, count, last); }
        Publish();
    }

    private void TakeNow(Uri url, string title, int count, DateTimeOffset last)
    {
        if (!IsWeb(url) || Kept(url) is not { } kept) return;
        var key = Identity(kept);
        if (key.Length == 0) return;
        if (_visits.TryGetValue(key, out var seen))
        {
            // The larger of the two, not their sum: the same browser brought in again must not count every visit twice.
            seen.Count = Math.Min(MostVisits, Math.Max(seen.Count, count));
            if (last > seen.Last) seen.Last = last;
            if (seen.Title.Length == 0) seen.Title = title;
        }
        else seen = new Visit { Url = kept.AbsoluteUri, Key = key, Title = title, Count = Math.Clamp(count, 0, MostVisits), Last = last };
        Set(key, seen);
        Remember(seen, kept);
        Prune();
    }

    /// After a batch of Takes.
    public void Settle() { lock (_lock) Save(); }

    /// A page's title usually lands a beat after the page does.
    public void Retitle(Uri url, string title)
    {
        lock (_lock) { Later(h => h.RetitleNow(url, title)); RetitleNow(url, title); }
        Publish();
    }

    private void RetitleNow(Uri url, string title)
    {
        var key = Identity(url);
        if (title.Length == 0 || !_visits.TryGetValue(key, out var seen) || seen.Title == title) return;
        // The last few places stay worked out: only this one's title changed. Unless it was a front door with no
        // title, which the list leaves out until it has one: then it may belong in it now.
        var first = seen.Title.Length == 0;
        var joins = first && _pageText.TryGetValue(key, out var text) && text.Homepage;
        seen.Title = title;
        var cached = joins ? null : _recentCache;
        Set(key, seen);
        _recentCache = cached?.Select(t => t.Key == key ? t with { Title = title } : t).ToList();
        Remember(seen);
        // Only the title a page lands with is written at once. One that keeps changing after it (an inbox counting
        // its mail) would rewrite the whole file each time, so it waits for the next visit's write, or for quitting.
        if (first) Save(); else _owed = true;
    }

    public void Forget()
    {
        lock (_lock) { Later(h => h.ForgetNow()); ForgetNow(); }
        Publish();
    }

    private void ForgetNow()
    {
        _visits = [];
        _pageText = [];
        _weights = [];
        _visitedHosts = [];
        Touch();
        Save();
    }

    public void Forget(string key)
    {
        lock (_lock) { Later(h => h.ForgetNow(key)); ForgetNow(key); }
        Publish();
    }

    private void ForgetNow(string key)
    {
        if (_visits.Remove(key)) Touch();
        ForgetText(key);
        Save();
    }

    // Reading.

    /// A front door credited by its pages' visits has no title of its own, and in a list of where you have been it
    /// is a second copy of every line.
    private bool Listed(Visit visit) => !(visit.Title.Length == 0 && _pageText.TryGetValue(visit.Key, out var text) && text.Homepage);

    private Trace? TraceOf(Visit visit) =>
        Parse(visit.Url) is { } url ? new Trace(visit.Key, _pageText.TryGetValue(visit.Key, out var text) ? text.Address : visit.Key, visit.Title, url, visit.Last, visit.Count) : null;

    /// Everywhere you have been, newest first, for the window that shows it.
    public IReadOnlyList<Trace> Everything(string typed = "")
    {
        var needle = typed.Trim(' ', '\t').ToLowerInvariant();
        lock (_lock)
            return _visits.Values.Where(Listed)
                .Where(v => needle.Length == 0 || (_pageText.TryGetValue(v.Key, out var text) && text.Searchable.Contains(needle, StringComparison.Ordinal))
                    || v.Title.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                .OrderByDescending(v => v.Last).Select(TraceOf).OfType<Trace>().ToList();
    }

    /// The last eight places, newest first, as the History menu lists them. One pass keeping the newest eight,
    /// rather than sorting the whole history for them.
    public IReadOnlyList<Trace> Recent()
    {
        lock (_lock)
        {
            if (_recentCache is { } cached) return cached;
            var newest = new List<(Visit Visit, Uri Url)>();
            foreach (var visit in _visits.Values)
            {
                if (!Listed(visit)) continue;
                if (newest.Count == 8 && visit.Last <= newest[^1].Visit.Last) continue;
                if (Parse(visit.Url) is not { } url) continue;
                var at = newest.FindIndex(n => visit.Last > n.Visit.Last);
                newest.Insert(at < 0 ? newest.Count : at, (visit, url));
                if (newest.Count > 8) newest.RemoveAt(8);
            }
            return _recentCache = newest.Select(n => new Trace(n.Visit.Key, _pageText.TryGetValue(n.Visit.Key, out var text) ? text.Address : n.Visit.Key, n.Visit.Title, n.Url, n.Visit.Last, n.Visit.Count)).ToList();
        }
    }

    /// Best matches first: score = fit × log(2 + popularity).
    public IReadOnlyList<Suggestion> Suggestions(string typed, int limit = 5)
    {
        var needle = Stripped(typed);
        // An empty field proposes nothing. A list of guesses in front of someone who has not yet said what they want
        // is noise, and it is in the way of the one thing they came here to do.
        if (needle.Length == 0) return [];

        var bytes = Encoding.UTF8.GetBytes(needle);
        var words = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Encoding.UTF8.GetBytes).ToArray();
        // Only the best few are kept while scanning, in order, and only those become suggestions.
        var best = new List<(Suggestion Suggestion, double Score)>();

        // Higher first, then the shorter key, then whichever came first: the order a stable sort of every match
        // would have left them in.
        static bool Ahead(double score, string key, (Suggestion Suggestion, double Score) other) =>
            score == other.Score ? Encoding.UTF8.GetByteCount(key) < Encoding.UTF8.GetByteCount(other.Suggestion.Key) : score > other.Score;

        void Offer(string key, double score, Func<Suggestion?> make)
        {
            if (best.Count == limit && best.Count > 0 && !Ahead(score, key, best[^1])) return;
            if (make() is not { } made) return;
            var at = best.FindIndex(b => Ahead(score, key, b));
            best.Insert(at < 0 ? best.Count : at, (made, score));
            if (best.Count > limit) best.RemoveAt(best.Count - 1);
        }

        lock (_lock)
        {
            var fade = Math.Exp(-(DateTimeOffset.UtcNow - _epoch).TotalSeconds / Fading);

            void Visited(Visit visit, PageText text, double fit)
            {
                var popularity = Math.Max(0, _weights.GetValueOrDefault(text.Trail[^1]) * fade);
                Offer(text.Address, fit * Math.Log(2 + popularity), () => Parse(visit.Url) is { } url ? new Suggestion(text.Address, visit.Title, url, SuggestionKind.Visited) : null);
            }

            static Suggestion KnownRow((string Host, string Title, byte[] HostBytes, byte[] Findable) site) => new(site.Host, site.Title, new Uri("https://" + site.Host), SuggestionKind.Known);

            foreach (var (key, text) in _pageText)
            {
                if (!Holds(words, text.FindableBytes) || Fit(bytes, words, text.SearchableBytes, text.FindableBytes) is not { } fit || !_visits.TryGetValue(key, out var visit)) continue;
                Visited(visit, text, fit);
            }
            foreach (var site in KnownText)
            {
                if (_visitedHosts.Contains(site.Host) || !Holds(words, site.Findable) || Fit(bytes, words, site.HostBytes, site.Findable) is not { } fit) continue;
                Offer(site.Host, fit * KnownWeight, () => KnownRow(site));
            }

            // Room left: a typo (#762), fit 1. Slowest, so only then, from three letters.
            if (best.Count >= limit || Letters(needle) < 3) return best.Select(b => b.Suggestion).ToList();
            foreach (var (key, text) in _pageText)
                if (Fuzzy.Match(words, text.FindableBytes) == 0 && _visits.TryGetValue(key, out var visit)) Visited(visit, text, 1);
            foreach (var site in KnownText)
                if (!_visitedHosts.Contains(site.Host) && Fuzzy.Match(words, site.Findable) == 0) Offer(site.Host, KnownWeight, () => KnownRow(site));
            return best.Select(b => b.Suggestion).ToList();
        }
    }

    /// Every word somewhere in the page.
    private static bool Holds(byte[][] words, byte[] findable) => words.All(w => Fuzzy.Contains(findable, w));

    /// 4 address starts with it, 3 a word of the host does, 2 every word starts a word of title or address, 1 found
    /// inside a word. Title and path from three letters: "ui" is in nearly every page.
    private static double? Fit(byte[] needle, byte[][] words, ReadOnlySpan<byte> address, byte[] findable)
    {
        if (address.StartsWith(needle)) return 4;
        var end = address.IndexOfAny((byte)'/', (byte)'?', (byte)'#');
        if (Fuzzy.StartsWord(needle, end < 0 ? address : address[..end])) return 3;
        if (needle.Length < 3) return null;
        return words.All(w => Fuzzy.StartsWord(w, findable)) ? 2 : 1;
    }

    /// What the field should draw greyed out after the caret: the rest of the best match, or nothing if it doesn't
    /// carry on from what was typed.
    public string? Completion(string typed, IReadOnlyList<Suggestion> options)
    {
        if (typed.Length == 0) return null;
        // One letter finishes only the top row, and only when it is the front door of a site you have been to: "x"
        // becomes x.com, as in Safari and Chrome (#696). A room inside a site, a name the app merely knows, or a
        // lower row waits for a second letter.
        var length = Letters(typed);
        var single = length == 1;
        if (single && options.Count > 0 && options[0].Kind != SuggestionKind.Visited) return null;
        foreach (var option in single ? options.Take(1) : options)
            foreach (var (key, target) in Endings(option))
            {
                var info = new StringInfo(key);
                if (info.LengthInTextElements < length) continue;
                if (single && key.IndexOfAny(['/', '?', '#']) >= 0) continue;
                var prefix = info.SubstringByTextElements(0, length);
                if (!string.Equals(prefix, typed, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = key[prefix.Length..];
                if (Address.UrlFrom(typed + rest) is not { } completed || Identity(completed) != target) continue;
                return rest.Length == 0 ? null : rest;
            }
        return null;
    }

    private static int Letters(string text) => new StringInfo(text).LengthInTextElements;

    /// What a row can be finished as, and the page that has to come of it. Its address as shown; and for a www site,
    /// the name without the www, as the field always finished it: "app" still becomes apple.com, and the site sends
    /// you on to its www. Only the www is let go of: the scheme, the port and the path's case still have to come out
    /// the same.
    private static List<(string Key, string Target)> Endings(Suggestion option)
    {
        List<(string, string)> found = [(option.Key, Identity(option.Url))];
        if (option.Key.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && Address.HasHost(option.Url) && option.Url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            found.Add((option.Key[4..], Identity(new UriBuilder(option.Url) { Host = option.Url.Host[4..] }.Uri)));
        return found;
    }

    /// Often, and lately. A month-old visit counts for about a third of a fresh one, which is roughly how long a
    /// habit takes to stop being one.
    private static double Score(Visit visit, DateTimeOffset now) => visit.Count * Math.Exp(-Math.Max(0, (now - visit.Last).TotalDays) / 30);

    // The file.

    private static LoadedHistory Read(string file, DateTimeOffset epoch)
    {
        var result = new LoadedHistory();
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        if (Decode(data) is not { } list) { result.Corrupt = true; return result; }

        // Keys written by an older Search can meet under the new rule: they are merged, never trusted to be unique.
        var loaded = new Dictionary<string, Visit>();
        foreach (var saved in list)
        {
            var visit = saved;
            if (Parse(visit.Url) is { } url)
            {
                // An address kept with a name and password in it, by a Search from before they were taken off,
                // loses them here.
                if (url.UserInfo.Length > 0 && Kept(url) is { } clean) { visit.Url = clean.AbsoluteUri; result.Cleaned = true; }
                visit.Key = Identity(url);
            }
            loaded[visit.Key] = loaded.TryGetValue(visit.Key, out var had) ? Merged(had, visit) : visit;
        }
        RehomeCredits(loaded);
        result.Visits = loaded;
        foreach (var visit in loaded.Values)
        {
            if (Parse(visit.Url) is not { } url) continue;
            var text = Text(url, visit.Title);
            text.Weight = visit.Count * Math.Exp((visit.Last - epoch).TotalSeconds / Fading);
            foreach (var prefix in text.Trail) result.Weights[prefix] = result.Weights.GetValueOrDefault(prefix) + text.Weight;
            result.PageText[visit.Key] = text;
            if (text.Host.Length > 0) result.VisitedHosts.Add(text.Host);
        }
        return result;
    }

    /// Every visit, or null if the file isn't a list of them. A count from a file edited by hand is held to a sane
    /// range, so adding to it can't overflow.
    private static List<Visit>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Visit>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("url", out var u) || SwiftJson.Text(u) is not { } url
                    || !e.TryGetProperty("key", out var k) || SwiftJson.Text(k) is not { } key
                    || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title
                    || !e.TryGetProperty("count", out var c) || SwiftJson.Long(c) is not { } count
                    || !e.TryGetProperty("last", out var l) || SwiftJson.Date(l) is not { } last) return null;
                list.Add(new Visit { Url = url, Key = key, Title = title, Count = (int)Math.Clamp(count, 0, MostVisits), Last = last });
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static byte[] Encode(IEnumerable<Visit> visits) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var v in visits)
        {
            w.WriteStartObject();
            w.WriteString("url", v.Url);
            w.WriteString("key", v.Key);
            w.WriteString("title", v.Title);
            w.WriteNumber("count", v.Count);
            SwiftJson.WriteDate(w, "last", v.Last);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private void Finish(Task<LoadedHistory> job)
    {
        lock (_lock) Apply(job);
        Publish();
        _loaded.TrySetResult();
    }

    /// Under the lock. Waits for the read if it is still going (quitting may need it before its turn comes).
    private void Apply(Task<LoadedHistory> job)
    {
        if (_loading != job) return;
        var result = job.Result;
        _loading = null;
        if (result.Corrupt) Store.Quarantine(_file);
        _pageText = result.PageText;
        _weights = result.Weights;
        _visitedHosts = result.VisitedHosts;
        _visits = result.Visits;
        Touch();
        // Mutations were also shown as they happened; replaying them over the file keeps their order, including a
        // clear followed by a new visit.
        var pending = _duringLoad;
        _duringLoad = [];
        foreach (var change in pending) change(this);
        // And written without them now, not at the next visit.
        if (result.Cleaned || _owed) Save();
    }

    /// The same page twice: its visits added up, the latest date, and a title if either had one.
    private static Visit Merged(Visit a, Visit b)
    {
        var kept = a.Last >= b.Last ? a : b;
        kept.Count = (int)Math.Min(MostVisits, (long)a.Count + b.Count);
        if (kept.Title.Length == 0) kept.Title = a.Last >= b.Last ? b.Title : a.Title;
        return kept;
    }

    /// Before 1.0.4, every deep page also credited https:// and its host without www, whatever the scheme, port or
    /// www the page was on. Such a credit, with no title and nothing else on its own origin, moves to the front door
    /// its pages actually used: left where it was, it would be offered next to the one new visits credit, the same
    /// site twice.
    private static void RehomeCredits(Dictionary<string, Visit> visits)
    {
        // Visits per front door, by host without www, from the deeper pages.
        var doors = new Dictionary<string, Dictionary<string, long>>();
        foreach (var visit in visits.Values)
        {
            if (Parse(visit.Url) is not { } url || !Address.HasHost(url) || IsHomepath(url) || Homepage(url) is not { } home) continue;
            var host = url.Host.ToLowerInvariant();
            var bare = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
            if (!doors.TryGetValue(bare, out var used)) doors[bare] = used = [];
            var door = Identity(home);
            used[door] = used.GetValueOrDefault(door) + visit.Count;
        }
        foreach (var (key, credit) in visits.ToList())
        {
            if (credit.Title.Length > 0 || Parse(credit.Url) is not { } url || url.Scheme != "https" || !url.IsDefaultPort || url.Query.Length > 0 || !IsHomepath(url) || !Address.HasHost(url)) continue;
            var host = url.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal) || !doors.TryGetValue(host, out var used) || used.ContainsKey(key)) continue;
            var door = used.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal).First().Key;
            if (Parse(door) is not { } doorUrl) continue;
            visits.Remove(key);
            var moved = new Visit { Url = doorUrl.AbsoluteUri, Key = door, Title = "", Count = credit.Count, Last = credit.Last };
            visits[door] = visits.TryGetValue(door, out var there) ? Merged(there, moved) : moved;
        }
    }

    /// Coalesced: a busy minute of browsing writes the file once, not thirty times, and never on the UI thread.
    private void Save()
    {
        _owed = true;
        if (_pausing) return;
        _pausing = true;
        _ = Task.Delay(Pause).ContinueWith(_ => { lock (_lock) { _pausing = false; Write(now: false); } }, TaskScheduler.Default);
    }

    /// Quitting: whatever the file doesn't have yet (the write still waiting out the pause, a title that changed
    /// since) made before the process ends rather than never.
    public void Flush()
    {
        lock (_lock)
        {
            if (_loading is { } job) Apply(job);
            Write(now: true);
        }
        Publish();
        _loaded.TrySetResult();
    }

    /// A cap, so the file can't grow without end. What goes is what has been visited least and longest ago. Memory
    /// also keeps its newest places through a session (see Prune), so the file cuts to the cap by score on its own.
    private void Write(bool now)
    {
        if (_loading is not null || !_owed) return;
        _owed = false;
        var date = DateTimeOffset.UtcNow;
        var snapshot = _visits.Values.ToArray();
        Disk.Write(_file, () => Encode(snapshot.OrderByDescending(v => Score(v, date)).Take(Cap)), now);
    }

    /// Somewhere to start on the first day, before there is any history to go on. Ranked below anything actually
    /// visited, and dropped from the list the moment you have been there yourself.
    private static readonly (string Host, string Title)[] KnownSites =
    [
        ("google.com", "Google"), ("mail.google.com", "Gmail"),
        ("drive.google.com", "Google Drive"), ("calendar.google.com", "Google Calendar"),
        ("maps.google.com", "Google Maps"), ("youtube.com", "YouTube"),
        ("github.com", "GitHub"), ("figma.com", "Figma"), ("vercel.com", "Vercel"),
        ("notion.so", "Notion"), ("linear.app", "Linear"), ("slack.com", "Slack"),
        ("discord.com", "Discord"), ("x.com", "X"), ("linkedin.com", "LinkedIn"),
        ("instagram.com", "Instagram"), ("reddit.com", "Reddit"),
        ("news.ycombinator.com", "Hacker News"), ("stackoverflow.com", "Stack Overflow"),
        ("claude.ai", "Claude"), ("chatgpt.com", "ChatGPT"),
        ("dribbble.com", "Dribbble"), ("behance.net", "Behance"),
        ("awwwards.com", "Awwwards"), ("mobbin.com", "Mobbin"),
        ("siteinspire.com", "SiteInspire"), ("are.na", "Are.na"),
        ("pinterest.com", "Pinterest"), ("framer.com", "Framer"),
        ("webflow.com", "Webflow"), ("developer.apple.com", "Apple Developer"),
        ("swift.org", "Swift"), ("npmjs.com", "npm"), ("supabase.com", "Supabase"),
        ("stripe.com", "Stripe"), ("shopify.com", "Shopify"),
        ("cloudflare.com", "Cloudflare"), ("netlify.com", "Netlify"),
        ("apple.com", "Apple"), ("spotify.com", "Spotify"), ("netflix.com", "Netflix"),
        ("wikipedia.org", "Wikipedia"), ("deepl.com", "DeepL"), ("loom.com", "Loom"),
        ("amazon.fr", "Amazon"), ("leboncoin.fr", "leboncoin"), ("lemonde.fr", "Le Monde"),
    ];

    /// The same, prepared once for the looser search.
    private static readonly (string Host, string Title, byte[] HostBytes, byte[] Findable)[] KnownText =
        KnownSites.Select(k => (k.Host, k.Title, Encoding.UTF8.GetBytes(k.Host), Encoding.UTF8.GetBytes(k.Title.ToLowerInvariant() + " " + k.Host))).ToArray();
}
