using System.Text.Json;

namespace Search.Core;

// What you have kept (Loot.swift, and the record parts of Fetching.swift). Downloading is the engine's; this is only
// the memory of it, so a file you fetched an hour ago is one click from Explorer rather than a hunt through a folder.
// The newest fifty, in downloads.json, the Mac's [{name, from, path, date, address?}].

/// A file that arrived. `From` is the host it came from; `Address` where, for Copy Address: null for a file listed
/// before that was noted, one from inside its page or from a private tab, and one whose address went with the history.
public sealed record Keep(string Name, string From, string Path, DateTimeOffset Date)
{
    public string? Address { get; init; }

    public string Id => Path;
    public bool StillThere => File.Exists(Path);

    /// A file saved from `source`, now. `keepsAddress` is false when its address was forgotten while it came in.
    public static Keep Saved(string file, Uri? source, bool keepsAddress = true) =>
        new(System.IO.Path.GetFileName(file), source is not null && Search.Core.Address.HasHost(source) ? source.Host : "", file, DateTimeOffset.UtcNow) { Address = keepsAddress ? AddressOf(source) : null };

    /// The address as another app can fetch it. A blob: or data: one means nothing outside its page, and a data: one
    /// is the whole file again. A password in the address goes in neither the list's file nor the clipboard.
    public static string? AddressOf(Uri? url) => url is { IsAbsoluteUri: true, Scheme: "http" or "https" } ? History.Kept(url)?.AbsoluteUri : null;
}

public sealed class Downloads : Observable
{
    /// More than anybody scrolls back through.
    public const int Most = 50;

    private readonly string _file;
    private IReadOnlyList<Keep> _kept = [];

    public Downloads(string file = "downloads.json") { _file = Store.File(file); Load(); }

    /// Newest first.
    public IReadOnlyList<Keep> Kept { get => _kept; private set { _kept = value; Changed(); Save(); } }

    /// In front, in place of one at the same path.
    public void Add(Keep keep) => Kept = _kept.Where(k => k.Path != keep.Path).Prepend(keep).Take(Most).ToList();

    public void Forget(Keep keep) => Kept = _kept.Where(k => k.Id != keep.Id).ToList();

    /// Only the list is emptied. Files you asked for are yours, and deleting them is Explorer's business, not a browser's.
    public void ForgetAll() => Kept = [];

    /// History cleared or a profile deleted: the addresses are a list of where you have been, and can carry a signed
    /// link. The files stay listed.
    public void ForgetAddresses() { if (_kept.Any(k => k.Address is not null)) Kept = _kept.Select(k => k with { Address = null }).ToList(); }

    /// Where a file of this name can go in `folder` without writing over one that is there: "report 2.pdf", " 3" and
    /// on (Browser.free).
    public static string Free(string name, string folder)
    {
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var candidate = System.IO.Path.Combine(folder, name);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++) candidate = System.IO.Path.Combine(folder, $"{stem} {n}{ext}");
        return candidate;
    }

    /// Whether a file can be made in `folder`, made first if it's missing (Browser.canWrite). Tried for real, with an
    /// empty file taken away at once: a folder that is gone, read-only, on a drive no longer there or kept out of by
    /// Controlled folder access can look writable to every other question, and WebView2 then fails the download as it
    /// starts.
    public static bool CanWrite(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var probe = System.IO.Path.Combine(folder, $".search-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        finally { try { File.Delete(probe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { } }
    }

    /// Whether a download can be asked for again from the start: only a plain GET or HEAD. One a form sent (a POST, a
    /// body) would send the form again.
    public static bool Replayable(string? method, bool hasBody) => !hasBody && (method ?? "GET").ToUpperInvariant() is "GET" or "HEAD";

    // The file.

    private void Load()
    {
        byte[] data;
        try { data = File.ReadAllBytes(_file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (Decode(data) is { } list) _kept = list; else Store.Quarantine(_file);
    }

    /// One after another, the newest last (see Disk).
    private void Save() { var snapshot = _kept; Disk.Write(_file, () => Encode(snapshot)); }

    public static byte[] Encode(IEnumerable<Keep> kept) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var keep in kept)
        {
            w.WriteStartObject();
            w.WriteString("name", keep.Name);
            w.WriteString("from", keep.From);
            w.WriteString("path", keep.Path);
            SwiftJson.WriteDate(w, "date", keep.Date);
            if (keep.Address is { } address) w.WriteString("address", address);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// Null when the file won't decode, as Swift's decoder sees it: every one needs its name, source, path and date.
    public static List<Keep>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Keep>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("name", out var n) || SwiftJson.Text(n) is not { } name
                    || !e.TryGetProperty("from", out var f) || SwiftJson.Text(f) is not { } from
                    || !e.TryGetProperty("path", out var p) || SwiftJson.Text(p) is not { } path
                    || !e.TryGetProperty("date", out var d) || SwiftJson.Date(d) is not { } date) return null;
                string? address = null;
                if (SwiftJson.Has(e, "address", out var a) && (address = SwiftJson.Text(a)) is null) return null;
                list.Add(new Keep(name, from, path, date) { Address = address });
            }
            return list;
        }
        catch (JsonException) { return null; }
    }
}
