using System.Text;
using System.Text.Json;

namespace Search.Core;

// What was brought over from each browser, and when (ImportRecord.swift): the panel says so beside the browser, and
// "Replace" takes back out exactly the bookmarks that came from it. Kept by the browser's name, whichever profile; only
// imports made since this was kept have one, and an older one is never guessed at. In the settings notes under
// "import.records", as the Mac keeps it: {name: {date, bookmarks, places, passwords, bookmarkIDs, spaces?, pinned?}}.

/// `BookmarkIds`: every bookmark and folder an import from it added, still to be found or not. `Spaces` and `Pinned`:
/// Arc's or Zen's spaces made, and pins and pinned tabs brought; null in a record from before they were kept.
public sealed record ImportRecord(DateTimeOffset Date)
{
    public int Bookmarks { get; init; }
    public int Places { get; init; }
    public int Passwords { get; init; }
    public IReadOnlyList<Guid> BookmarkIds { get; init; } = [];
    public int? Spaces { get; init; }
    public int? Pinned { get; init; }
}

public sealed class ImportRecords(IDictionary<string, string>? settings = null)
{
    private const string Key = "import.records";
    private readonly IDictionary<string, string> _settings = settings ?? Bookmarks.SharedSettings;

    public ImportRecord? Of(string name) => All().GetValueOrDefault(name);

    /// What an import from `name` just added, on top of what was noted before.
    public void Note(string name, IReadOnlyList<Guid>? bookmarkIds = null, int bookmarks = 0, int places = 0, int passwords = 0, int spaces = 0, int pinned = 0, DateTimeOffset? now = null)
    {
        var all = All();
        var record = all.GetValueOrDefault(name) ?? new ImportRecord(now ?? DateTimeOffset.UtcNow);
        all[name] = record with
        {
            Date = now ?? DateTimeOffset.UtcNow,
            Bookmarks = record.Bookmarks + bookmarks,
            Places = record.Places + places,
            Passwords = record.Passwords + passwords,
            BookmarkIds = [.. record.BookmarkIds, .. bookmarkIds ?? []],
            Spaces = spaces > 0 ? (record.Spaces ?? 0) + spaces : record.Spaces,
            Pinned = pinned > 0 ? (record.Pinned ?? 0) + pinned : record.Pinned,
        };
        Save(all);
    }

    /// Its bookmarks taken back out: what is recorded of them goes too.
    public void ForgetBookmarks(string name)
    {
        var all = All();
        if (!all.TryGetValue(name, out var record)) return;
        all[name] = record with { BookmarkIds = [], Bookmarks = 0 };
        Save(all);
    }

    private Dictionary<string, ImportRecord> All()
    {
        var all = new Dictionary<string, ImportRecord>();
        if (!_settings.TryGetValue(Key, out var text)) return all;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var e = entry.Value;
                if (!SwiftJson.Has(e, "date", out var d) || SwiftJson.Date(d) is not { } date) continue;
                int Count(string key) => SwiftJson.Has(e, key, out var v) && SwiftJson.Int(v) is { } n ? n : 0;
                int? Maybe(string key) => SwiftJson.Has(e, key, out var v) ? SwiftJson.Int(v) : null;
                var ids = SwiftJson.Has(e, "bookmarkIDs", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(SwiftJson.Uuid).OfType<Guid>().ToList() : [];
                all[entry.Name] = new ImportRecord(date) { Bookmarks = Count("bookmarks"), Places = Count("places"), Passwords = Count("passwords"), BookmarkIds = ids, Spaces = Maybe("spaces"), Pinned = Maybe("pinned") };
            }
        }
        catch (JsonException) { }
        return all;
    }

    private void Save(Dictionary<string, ImportRecord> all) => _settings[Key] = Encoding.UTF8.GetString(SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        foreach (var (name, record) in all)
        {
            w.WriteStartObject(name);
            SwiftJson.WriteDate(w, "date", record.Date);
            w.WriteNumber("bookmarks", record.Bookmarks);
            w.WriteNumber("places", record.Places);
            w.WriteNumber("passwords", record.Passwords);
            w.WriteStartArray("bookmarkIDs");
            foreach (var id in record.BookmarkIds) w.WriteStringValue(SwiftJson.Uuid(id));
            w.WriteEndArray();
            if (record.Spaces is { } spaces) w.WriteNumber("spaces", spaces);
            if (record.Pinned is { } pinned) w.WriteNumber("pinned", pinned);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }));
}
