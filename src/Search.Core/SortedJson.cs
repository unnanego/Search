using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Search.Core;

// JSON text as Foundation's JSONEncoder writes it with .sortedKeys (SyncMerge.swift's `text` and `encode`), for the
// files where the same value has to be the same characters on a Mac and a PC. Sync compares its items as text, so a
// PC writing {"name":"A","colour":1} where a Mac writes {"colour":1,"name":"A"} would look like an edit on every pass,
// and the two would take turns stamping it for ever.
//
// Keys in Foundation's order (digits compared as numbers, case ignored, then literally to break a tie); strings with
// only the quote, the backslash and control characters escaped, and "/" unless asked not to; nothing else escaped, so
// accents and emoji go as they are; a whole number without ".0", any other in its shortest form that reads back the
// same. Also Swift's own String ordering, which Sync's tie-breaks use.
public static class SortedJson
{
    public static string Bool(bool value) => value ? "true" : "false";
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// As Swift writes a Double: 135, not 135.0; 0.5; 1e-05.
    public static string Number(double value) =>
        Math.Abs(value) < 1e15 && value == Math.Floor(value) ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');

    public static string String(string text, bool escapeSlashes = false)
    {
        var made = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
            switch (c)
            {
                case '"': made.Append("\\\""); break;
                case '\\': made.Append("\\\\"); break;
                case '/': made.Append(escapeSlashes ? "\\/" : "/"); break;
                case '\b': made.Append("\\b"); break;
                case '\f': made.Append("\\f"); break;
                case '\n': made.Append("\\n"); break;
                case '\r': made.Append("\\r"); break;
                case '\t': made.Append("\\t"); break;
                case < ' ': made.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: made.Append(c); break;
            }
        return made.Append('"').ToString();
    }

    public static string Array(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";
    public static string Strings(IEnumerable<string> items, bool escapeSlashes = false) => Array(items.Select(i => String(i, escapeSlashes)));

    /// An object of the fields given, each value already JSON; a null value is a key left out, as encodeIfPresent leaves it.
    public static string Object(IEnumerable<(string Key, string? Json)> fields, bool escapeSlashes = false) =>
        "{" + string.Join(",", fields.Where(f => f.Json is not null).OrderBy(f => f.Key, KeyOrder).Select(f => String(f.Key, escapeSlashes) + ":" + f.Json)) + "}";

    /// Foundation's order for sorted keys: [.numeric, .caseInsensitive, .forcedOrdering].
    public static readonly IComparer<string> KeyOrder = Comparer<string>.Create(CompareKeys);

    public static int CompareKeys(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (si, sj) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var (x, y) = (a[si..i].TrimStart('0'), b[sj..j].TrimStart('0'));
                if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
                if (string.CompareOrdinal(x, y) is var digits and not 0) return Math.Sign(digits);
                continue;
            }
            var (ca, cb) = (char.ToLowerInvariant(a[i]), char.ToLowerInvariant(b[j]));
            if (ca != cb) return ca < cb ? -1 : 1;
            i++;
            j++;
        }
        if ((a.Length - i == 0) != (b.Length - j == 0)) return a.Length - i == 0 ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// Swift's `<` on String: Unicode scalars of the canonical (NFC) form, one by one.
    public static readonly IComparer<string> SwiftOrder = Comparer<string>.Create(CompareSwift);

    public static int CompareSwift(string a, string b)
    {
        if (!a.IsNormalized()) a = a.Normalize();
        if (!b.IsNormalized()) b = b.Normalize();
        var (x, y) = (a.EnumerateRunes(), b.EnumerateRunes());
        while (true)
        {
            var (more, other) = (x.MoveNext(), y.MoveNext());
            if (!more || !other) return more == other ? 0 : more ? 1 : -1;
            if (x.Current.Value != y.Current.Value) return x.Current.Value < y.Current.Value ? -1 : 1;
        }
    }

    /// Whether two JSON texts say the same thing: the same keys with the same values in any order, numbers by value.
    /// Text that isn't JSON is the same only as itself.
    public static bool Same(string a, string b)
    {
        if (a == b) return true;
        try
        {
            using var x = JsonDocument.Parse(a);
            using var y = JsonDocument.Parse(b);
            return Same(x.RootElement, y.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var mine = new Dictionary<string, JsonElement>();
                foreach (var p in a.EnumerateObject()) mine[p.Name] = p.Value;
                var count = 0;
                foreach (var p in b.EnumerateObject()) { count++; if (!mine.TryGetValue(p.Name, out var v) || !Same(v, p.Value)) return false; }
                return count == mine.Count;
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number: return a.TryGetDouble(out var m) && b.TryGetDouble(out var n) ? m.Equals(n) : a.GetRawText() == b.GetRawText();
            default: return true;
        }
    }
}
