using System.Text.Json;

namespace Search.Core;

// The extensions Search has added, as the list in Settings shows them (Installed and `list` in Extensions.swift).
//
// Kept in Extensions\installed.json in the store's folder, beside a folder for each one (Extensions\<id>) holding the
// copy WebView2 loads. The file is the Mac's: a list of {"id", "name", "version", "enabled", "fromStore",
// "permissions"}, and "pinned" and "source" only when they are set, as Swift's encoder leaves a nil out.

/// One installed extension.
/// `Id`: the Chrome Web Store id, or for one loaded from a folder the id its own key gives it.
/// `Permissions`: what it was installed with (Manifest.Grants), so an update that asks for more is asked about rather
/// than slipped through.
/// `Pinned`: kept in the toolbar rather than only in the menu. Optional, so a list written before pinning still reads.
/// `Source`: for one loaded from a folder, where that folder is, so Reload can bring the author's latest edits in.
public sealed record Installed(string Id, string Name, string Version, bool Enabled, bool FromStore, IReadOnlyList<string> Permissions, bool? Pinned = null, string? Source = null)
{
    public bool Equals(Installed? other) =>
        other is not null && (Id, Name, Version, Enabled, FromStore, Pinned, Source) == (other.Id, other.Name, other.Version, other.Enabled, other.FromStore, other.Pinned, other.Source) && Permissions.SequenceEqual(other.Permissions);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Version, Enabled, FromStore, Pinned, Source, Permissions.Count);
}

public sealed class ExtensionList : Observable
{
    public const string FileName = "installed.json";

    private IReadOnlyList<Installed> _items = [];

    /// `folder` is where the extensions are kept: Extensions in the store's folder unless a test says otherwise.
    public ExtensionList(string? folder = null)
    {
        Folder = folder ?? Store.File("Extensions");
        byte[]? data = null;
        try { data = File.ReadAllBytes(ListFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (data is null) return;
        if (Decode(data) is { } saved) _items = saved; else Store.Quarantine(ListFile);
    }

    public string Folder { get; }
    public string ListFile => Path.Combine(Folder, FileName);

    /// Where one extension's copy is. An id is letters, digits and dashes; anything else would be a path.
    public string FolderFor(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? Path.Combine(Folder, id) : throw new ArgumentException($"Not an extension id: {id}", nameof(id));

    /// In the order they were added.
    public IReadOnlyList<Installed> Items { get => _items; private set { _items = value; Changed(); } }

    public Installed? Find(string id) => _items.FirstOrDefault(i => i.Id == id);

    /// Added at the end, or in the place of the one with its id.
    public void Put(Installed item)
    {
        var at = _items.ToList().FindIndex(i => i.Id == item.Id);
        if (at >= 0 && _items[at] == item) return;
        Items = at < 0 ? [.. _items, item] : _items.Select((i, n) => n == at ? item : i).ToList();
        Save();
    }

    public bool Remove(string id)
    {
        if (Find(id) is null) return false;
        Items = _items.Where(i => i.Id != id).ToList();
        Save();
        return true;
    }

    public void SetPinned(string id, bool on) { if (Find(id) is { } item) Put(item with { Pinned = on }); }
    public void SetEnabled(string id, bool on) { if (Find(id) is { } item) Put(item with { Enabled = on }); }

    private void Save() { var snapshot = _items; Disk.Write(ListFile, () => Encode(snapshot)); }

    // The file.

    public static byte[] Encode(IEnumerable<Installed> items) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var i in items)
        {
            w.WriteStartObject();
            w.WriteString("id", i.Id);
            w.WriteString("name", i.Name);
            w.WriteString("version", i.Version);
            w.WriteBoolean("enabled", i.Enabled);
            w.WriteBoolean("fromStore", i.FromStore);
            w.WriteStartArray("permissions");
            foreach (var p in i.Permissions) w.WriteStringValue(p);
            w.WriteEndArray();
            if (i.Pinned is { } pinned) w.WriteBoolean("pinned", pinned);
            if (i.Source is { } source) w.WriteString("source", source);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// Null when the file won't decode, as the Mac's decoder sees it: every one needs all but pinned and source.
    public static List<Installed>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = new List<Installed>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Text(e, "id") is not { } id || Text(e, "name") is not { } name || Text(e, "version") is not { } version
                    || Bool(e, "enabled") is not { } enabled || Bool(e, "fromStore") is not { } fromStore
                    || !e.TryGetProperty("permissions", out var p) || p.ValueKind != JsonValueKind.Array || p.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return null;
                bool? pinned = null;
                string? source = null;
                if (SwiftJson.Has(e, "pinned", out var pin)) { if (SwiftJson.Bool(pin) is not { } b) return null; pinned = b; }
                if (SwiftJson.Has(e, "source", out var src)) { if (SwiftJson.Text(src) is not { } s) return null; source = s; }
                items.Add(new Installed(id, name, version, enabled, fromStore, p.EnumerateArray().Select(x => x.GetString()!).ToList(), pinned, source));
            }
            return items;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? SwiftJson.Text(v) : null;
    private static bool? Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? SwiftJson.Bool(v) : null;
}
