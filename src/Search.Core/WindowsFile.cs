using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Search.Core;

// Several windows' saved state (Windows.swift's WindowRecord and windows.json).
//
// The session: session.json and the other spaces' files belong to the oldest window still open of each profile, so
// an older version reads a real window's tabs, and every other window is a line in windows.json, its frame, its space
// and its row of tabs in every space it has one in. The first line is the oldest window's: its frame and space alone,
// its rows being the session files. A window belongs to one profile, the profile of the space it shows: the Mac writes
// nothing else for it, and neither does this. The lines also keep what OneWindow.swift and Fold.swift add (merge,
// tucked, folded), read and written back untouched whether or not this version does anything with them.
//
// windows.json is the Mac's, key for key: a list of {id, frame?, space, rows, merge?, tucked?, folded?}, rows keyed by
// the space's uppercase id, each a session file's shape (Session.Encode). Read as leniently as the Mac's decoder
// reads it: a field that doesn't read is its default, and only a line that isn't an object spoils the list.

/// A window's frame as AppKit writes it (NSStringFromRect): "{{x, y}, {width, height}}", in points.
public readonly record struct WindowFrame(double X, double Y, double Width, double Height)
{
    private static readonly Regex Numbers = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);

    public override string ToString() => $"{{{{{N(X)}, {N(Y)}}}, {{{N(Width)}, {N(Height)}}}}}";
    private static string N(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);

    /// As NSRectFromString reads one: its first four numbers, zero for any missing.
    public static WindowFrame Parse(string? text)
    {
        var found = text is null ? [] : Numbers.Matches(text).Select(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).Take(4).ToList();
        while (found.Count < 4) found.Add(0);
        return new(found[0], found[1], found[2], found[3]);
    }
}

/// A window's saved state: where it was, which space it showed, and its row of tabs in every space it has one in.
/// The oldest window's rows are in the session files instead, and its line only holds the frame.
public sealed record WindowRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Frame { get; init; }
    public Guid Space { get; init; } = Session.FirstSpace;
    /// Rows by space, keyed by the space's uppercase id.
    public IReadOnlyDictionary<string, Session.Shape> Rows { get; init; } = new Dictionary<string, Session.Shape>();
    /// The merged window it was one of, and whether it was tucked behind the one on screen (OneWindow.swift).
    public Guid? Merge { get; init; }
    public bool? Tucked { get; init; }
    /// Its column, or the strip across the top, folded away. Null, as in any line written before it was kept: shown.
    public bool? Folded { get; init; }

    /// Its frame, if it is one worth putting a window at: wider and taller than 100.
    public WindowFrame? Rect => Frame is null ? null : WindowFrame.Parse(Frame) is { Width: > 100, Height: > 100 } rect ? rect : null;

    /// Its row in a space, as saved here: empty for a space it has none in.
    public Session.Shape Row(Guid space) => Rows.TryGetValue(SwiftJson.Uuid(space), out var row) ? row : Session.Shape.Empty;
}

public static class WindowsFile
{
    public const string FileName = "windows.json";

    /// The profile a window is in: its space's.
    public static Guid ProfileOf(WindowRecord record, Spaces spaces) => spaces.ProfileOf(record.Space);

    /// Which windows hold their profile's session files, given every open window oldest first with its profile and
    /// whether it is a pop-up: for each profile, its oldest window that isn't a pop-up. The others keep their rows in
    /// their own lines.
    public static HashSet<Guid> FileHolders(IEnumerable<(Guid Window, Guid Profile, bool Popup)> oldestFirst)
    {
        var held = new HashSet<Guid>();
        var holders = new HashSet<Guid>();
        foreach (var (window, profile, popup) in oldestFirst)
            if (!popup && held.Add(profile)) holders.Add(window);
        return holders;
    }

    /// windows.json's lines: the oldest window's first, its rows left to the session files, then every other window
    /// whole; and, while last time's other windows haven't been brought back (`waiting`, read at launch), those as
    /// they were written, not dropped. Pop-ups are never among them.
    public static List<WindowRecord> Lines(WindowRecord oldest, IEnumerable<WindowRecord> others, IReadOnlyList<WindowRecord>? waiting = null) =>
        [oldest with { Rows = new Dictionary<string, Session.Shape>() }, .. others, .. (waiting ?? []).Skip(1)];

    /// Start with a fresh window: one window, the first, where it was; the others' tabs don't come back.
    public static List<WindowRecord> Fresh(IReadOnlyList<WindowRecord> records) => [.. records.Take(1)];

    public static List<WindowRecord> Read(string file = FileName)
    {
        try { return Decode(File.ReadAllBytes(Store.File(file))) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// `now` writes on the calling thread: quitting doesn't wait for a queue.
    public static Task Write(IReadOnlyList<WindowRecord> records, bool now = false, string file = FileName)
    {
        var snapshot = records.ToList();
        return Disk.Write(Store.File(file), () => Encode(snapshot), now);
    }

    /// As the Mac's JSONEncoder writes them, key order included. The rows are Session.Encode's own bytes, slashes
    /// already escaped, so the rest is written without escaping them again (frames and ids have none).
    public static byte[] Encode(IEnumerable<WindowRecord> records)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var r in records)
            {
                w.WriteStartObject();
                SwiftJson.WriteUuid(w, "id", r.Id);
                if (r.Frame is { } frame) w.WriteString("frame", frame);
                SwiftJson.WriteUuid(w, "space", r.Space);
                w.WriteStartObject("rows");
                foreach (var (space, shape) in r.Rows) { w.WritePropertyName(space); w.WriteRawValue(Session.Encode(shape)); }
                w.WriteEndObject();
                if (r.Merge is { } merge) SwiftJson.WriteUuid(w, "merge", merge);
                if (r.Tucked is { } tucked) w.WriteBoolean("tucked", tucked);
                if (r.Folded is { } folded) w.WriteBoolean("folded", folded);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return buffer.ToArray();
    }

    /// Null when the list won't decode: it has to be a list of objects. Within one, each field is read on its own
    /// and is its default when it doesn't read; the rows go whole or not at all, as the Mac's try? has them.
    public static List<WindowRecord>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var made = new List<WindowRecord>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                made.Add(new WindowRecord
                {
                    Id = e.TryGetProperty("id", out var i) && SwiftJson.Uuid(i) is { } id ? id : Guid.NewGuid(),
                    Frame = e.TryGetProperty("frame", out var f) ? SwiftJson.Text(f) : null,
                    Space = e.TryGetProperty("space", out var s) && SwiftJson.Uuid(s) is { } space ? space : Session.FirstSpace,
                    Rows = e.TryGetProperty("rows", out var r) ? Rows(r) : new Dictionary<string, Session.Shape>(),
                    Merge = e.TryGetProperty("merge", out var m) ? SwiftJson.Uuid(m) : null,
                    Tucked = e.TryGetProperty("tucked", out var t) ? SwiftJson.Bool(t) : null,
                    Folded = e.TryGetProperty("folded", out var o) ? SwiftJson.Bool(o) : null,
                });
            }
            return made;
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, Session.Shape> Rows(JsonElement e)
    {
        var rows = new Dictionary<string, Session.Shape>();
        if (e.ValueKind != JsonValueKind.Object) return rows;
        foreach (var p in e.EnumerateObject())
            if (Session.Decode(Encoding.UTF8.GetBytes(p.Value.GetRawText())) is { } shape) rows[p.Name] = shape; else return [];
        return rows;
    }
}
