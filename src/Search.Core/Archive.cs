using System.Globalization;
using System.Text.Json;

namespace Search.Core;

// The tabs Search closed by itself, as Arc's archive keeps them (Archive.swift).
//
// With tabs closing themselves (Closing.cs), a tab nobody has looked at for its time closes. Ctrl+Shift+T holds a
// dozen closes, and History has each page among everything else; here they stay together, newest first, for thirty
// days or the last five hundred: History › Archived Tabs lists them, with a search, and a click opens one again in the
// space on screen. Only what Search closed (a tab you closed yourself is one you meant to), never a private tab, and
// each profile has its own: archive.json for Personal, archive-<UUID>.json for each other one.
//
// The file is the Mac's: a list of {"id": UUID, "url": "...", "title": "...", "closed": "2026-11-21T09:30:00Z"}, newest
// first. Its dates are ISO 8601 (the Mac's encoder is set to that here), not the reference-date seconds of the
// other files.

/// One tab Search closed, as the archive keeps it.
public sealed record Archived(Guid Id, Uri Url, string Title, DateTimeOffset Closed)
{
    /// Its title, or for one without, its address as the field shows it without www.
    public string Label => Title.Length == 0 ? Address.WithoutWww(Address.Editable(Url)) : Title;
    public string Site => Address.WithoutWww(Url.IsAbsoluteUri && Url.Host.Length > 0 ? Url.Host : Address.Editable(Url));
}

public sealed class Archive : Observable
{
    /// Kept at most: far more than anybody scrolls back through, and a small file.
    public const int Cap = 500;
    /// Kept for a month: past that, History has it.
    public static readonly TimeSpan Lasts = TimeSpan.FromDays(30);

    private static readonly Dictionary<Guid, Archive> _made = [];
    private readonly string _fileName;
    private IReadOnlyList<Archived> _tabs = [];
    private bool _retired;

    public Archive(string file = "archive.json", DateTimeOffset? now = null)
    {
        _fileName = file;
        byte[]? data = null;
        try { data = File.ReadAllBytes(FilePath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (data is null) return;
        if (Decode(data) is { } saved) _tabs = Trimmed(saved, now ?? DateTimeOffset.UtcNow); else Store.Quarantine(FilePath);
    }

    /// Newest first.
    public IReadOnlyList<Archived> Tabs { get => _tabs; private set { _tabs = value; Changed(); } }

    private string FilePath => Store.File(_fileName);

    /// archive.json for Personal, one of its own for each other profile, as history is.
    public static string FileName(Guid profile) => profile == Pins.PersonalProfile ? "archive.json" : $"archive-{SwiftJson.Uuid(profile)}.json";

    /// Each profile's, made the first time it is asked for.
    public static Archive Of(Guid profile)
    {
        lock (_made)
        {
            if (_made.TryGetValue(profile, out var archive)) return archive;
            return _made[profile] = new Archive(FileName(profile));
        }
    }

    /// A profile deleted: its archive emptied and its file gone, opened this run or not.
    public static void Forget(Guid profile)
    {
        lock (_made) { if (_made.Remove(profile, out var archive)) archive._retired = true; }
        var file = Store.File(FileName(profile));
        Disk.Write(file, () => { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; });
    }

    /// What a tab closing by itself leaves here: its address as the tab knows it, asleep or not, when it is a web page
    /// or a file. Null for a private tab, Search's own pages and anything else.
    public static Uri? Takes(Uri? url, bool @private) => !@private && url is { IsAbsoluteUri: true } && url.Scheme is "http" or "https" or "file" ? url : null;

    /// A tab Search just closed. A page already here moves to the top. Its name and password never reach the file.
    public void Add(Uri url, string title, DateTimeOffset? closed = null, DateTimeOffset? now = null)
    {
        if (_retired || History.Kept(url) is not { } kept) return;
        var when = closed ?? DateTimeOffset.UtcNow;
        Tabs = Trimmed([new Archived(Guid.NewGuid(), kept, title, when), .. _tabs.Where(t => t.Url.AbsoluteUri != kept.AbsoluteUri)], now ?? DateTimeOffset.UtcNow);
        Save();
    }

    /// Opened again from the list (the window opens it in the space on screen), or removed by hand.
    public void Remove(Guid id)
    {
        if (!_tabs.Any(t => t.Id == id)) return;
        Tabs = _tabs.Where(t => t.Id != id).ToList();
        Save();
    }

    public void Clear()
    {
        if (_tabs.Count == 0) return;
        Tabs = [];
        Save();
    }

    /// What the list shows: past the month, nothing, even before anything is next added; and, with words typed, what
    /// has all of them in its title or address.
    public IReadOnlyList<Archived> Listed(string hunt = "", DateTimeOffset? now = null)
    {
        var words = hunt.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Trimmed(_tabs, now ?? DateTimeOffset.UtcNow).Where(t => (t.Title + " " + t.Url.OriginalString).ToLowerInvariant() is var text && words.All(w => text.Contains(w, StringComparison.Ordinal))).ToList();
    }

    /// Newest first, a month at most, and no more than the cap.
    public static IReadOnlyList<Archived> Trimmed(IEnumerable<Archived> list, DateTimeOffset now) => list.Where(t => t.Closed >= now - Lasts).OrderByDescending(t => t.Closed).Take(Cap).ToList();

    private void Save()
    {
        if (_retired) return;
        var snapshot = _tabs;
        Disk.Write(FilePath, () => Encode(snapshot));
    }

    // The file.

    public static byte[] Encode(IEnumerable<Archived> tabs) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var t in tabs)
        {
            w.WriteStartObject();
            SwiftJson.WriteUuid(w, "id", t.Id);
            w.WriteString("url", t.Url.OriginalString);
            w.WriteString("title", t.Title);
            w.WriteString("closed", Iso(t.Closed));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// As the Mac's encoder writes a date here: UTC, to the second.
    public static string Iso(DateTimeOffset date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Iso(string? text) =>
        DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// Null when the file won't decode, as the Mac's decoder sees it: every tab needs its id, address, title and date.
    public static List<Archived>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var tabs = new List<Archived>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id || !e.TryGetProperty("url", out var u) || SwiftJson.Text(u) is not { } text
                    || !Uri.TryCreate(text, UriKind.Absolute, out var url) || !e.TryGetProperty("title", out var t) || SwiftJson.Text(t) is not { } title
                    || !e.TryGetProperty("closed", out var c) || Iso(SwiftJson.Text(c)) is not { } closed) return null;
                tabs.Add(new Archived(id, url, title, closed));
            }
            return tabs;
        }
        catch (JsonException) { return null; }
    }
}
