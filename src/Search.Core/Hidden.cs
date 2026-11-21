using System.Text.Json;

namespace Search.Core;

// Taking things off a page and keeping them off (Curtain.swift, the storage side; Hidden.swift is its panel).
//
// Point at a cookie bar or a newsletter overlay and it goes, and is still gone next time. Remembered by site (the host
// without its www., not the registrable domain: news.example.com and example.com are kept apart), as a list of
// selectors in hidden.json, the Mac's {"host": [{selector, label, note?, date}]}. Put back on by a stylesheet injected
// before the page has drawn a frame, so nothing is seen appearing and vanishing.

/// One thing taken off a site. `Label` is what it was in words; `Note` its size and where it sat, measured when it was
/// hidden, since a hidden thing has no size to measure later.
public sealed record Veil(string Selector, string Label, string? Note, DateTimeOffset Date)
{
    public string Id => Selector;
}

public sealed class Hidden : Observable
{
    private readonly string _file;
    private Dictionary<string, IReadOnlyList<Veil>> _byHost = [];

    public Hidden(string file = "hidden.json") { _file = Store.File(file); Load(); }

    public IReadOnlyDictionary<string, IReadOnlyList<Veil>> ByHost => _byHost;

    /// The site a page is kept under: its host, lowercased, without www. Null for an address with no host.
    public static string? Host(Uri? url)
    {
        if (url is null || !Address.HasHost(url)) return null;
        var host = url.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    public IReadOnlyList<Veil> Veils(string? host) => host is not null && _byHost.TryGetValue(host, out var list) ? list : [];

    public void Hide(string selector, string label, string note, string host)
    {
        var list = Veils(host);
        if (list.Any(v => v.Selector == selector)) return;
        Put(host, [.. list, new Veil(selector, label, note, DateTimeOffset.UtcNow)]);
    }

    /// Put one back.
    public void Restore(Veil veil, string host) => Put(host, Veils(host).Where(v => v.Selector != veil.Selector).ToList());

    /// Put the last one back: Ctrl+Z, while you are still pointing at things.
    public Veil? Undo(string host)
    {
        var list = Veils(host);
        if (list.Count == 0) return null;
        Put(host, list.Take(list.Count - 1).ToList());
        return list[^1];
    }

    public void RestoreAll(string host) => Put(host, []);

    /// A site left with nothing hidden is forgotten.
    private void Put(string host, IReadOnlyList<Veil> list)
    {
        var next = new Dictionary<string, IReadOnlyList<Veil>>(_byHost);
        if (list.Count == 0) next.Remove(host); else next[host] = list;
        _byHost = next;
        Changed(nameof(ByHost));
        // One after another, the newest last (see Disk); the snapshot is never changed after this.
        Disk.Write(_file, () => Encode(next));
    }

    /// The stylesheet for a site. Each selector stands alone in its own rule: one the browser can't parse would
    /// otherwise take the whole list down with it. One may be left out, which is how a row in the list shows you what
    /// it offers to bring back without bringing it back.
    public string Css(string? host, string? without = null) =>
        string.Join("\n", Veils(host).Where(v => v.Selector != without).Select(v => $"{v.Selector} {{ display: none !important; }}"));

    /// The script that puts the stylesheet in before the document has a body (Veiling.style).
    public static string Script(string css)
    {
        var escaped = css.Replace("\\", "\\\\").Replace("`", "\\`").Replace("$", "\\$");
        return $$"""
            (function () {
              var sheet = document.getElementById('office-veil');
              if (!sheet) {
                sheet = document.createElement('style');
                sheet.id = 'office-veil';
                (document.head || document.documentElement).appendChild(sheet);
              }
              sheet.textContent = `{{escaped}}`;
            })();
            """;
    }

    // The file.

    private void Load()
    {
        byte[] data;
        try { data = File.ReadAllBytes(_file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (Decode(data) is { } stored) _byHost = stored; else Store.Quarantine(_file);
    }

    public static byte[] Encode(IReadOnlyDictionary<string, IReadOnlyList<Veil>> byHost) => SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        foreach (var (host, veils) in byHost)
        {
            w.WriteStartArray(host);
            foreach (var veil in veils)
            {
                w.WriteStartObject();
                w.WriteString("selector", veil.Selector);
                w.WriteString("label", veil.Label);
                if (veil.Note is { } note) w.WriteString("note", note);
                SwiftJson.WriteDate(w, "date", veil.Date);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    });

    /// Null when the file won't decode, as Swift's decoder sees it: every veil needs its selector, label and date.
    public static Dictionary<string, IReadOnlyList<Veil>>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var byHost = new Dictionary<string, IReadOnlyList<Veil>>();
            foreach (var site in document.RootElement.EnumerateObject())
            {
                if (site.Value.ValueKind != JsonValueKind.Array) return null;
                var veils = new List<Veil>();
                foreach (var e in site.Value.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object
                        || !e.TryGetProperty("selector", out var s) || SwiftJson.Text(s) is not { } selector
                        || !e.TryGetProperty("label", out var l) || SwiftJson.Text(l) is not { } label
                        || !e.TryGetProperty("date", out var d) || SwiftJson.Date(d) is not { } date) return null;
                    string? note = null;
                    if (SwiftJson.Has(e, "note", out var n) && (note = SwiftJson.Text(n)) is null) return null;
                    veils.Add(new Veil(selector, label, note, date));
                }
                byHost[site.Name] = veils;
            }
            return byHost;
        }
        catch (JsonException) { return null; }
    }
}
