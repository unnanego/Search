using System.Text;

namespace Search.Core;

// Typos in the address field (Fuzzy.swift): a word with a letter wrong, missing or swapped still finds what it meant
// (#762). Only a fallback, ranked below exact matches.
//
// Works on UTF-8 bytes because it runs over the whole history on every key.
public static class Fuzzy
{
    /// 1 if every word is in `text` as typed, 0 if one needed a typo forgiven, null if one isn't there. Both are
    /// lowercased.
    public static int? Match(IReadOnlyList<byte[]> typed, ReadOnlySpan<byte> text)
    {
        var slipped = false;
        foreach (var word in typed)
        {
            if (Contains(text, word)) continue;
            if (!Nearly(word, text)) return null;
            slipped = true;
        }
        return slipped ? 0 : 1;
    }

    public static int? Match(IReadOnlyList<byte[]> typed, string text) => Match(typed, Encoding.UTF8.GetBytes(text));

    /// Whether a word in `text` starts with `typed`, allowing one typo, or two in long words. None under four
    /// letters, where a guess is noise.
    public static bool Nearly(byte[] typed, string text) => Nearly(typed, Encoding.UTF8.GetBytes(text));

    /// "dwn" for downloads: the letters in order, starting with the first.
    public static bool Abbreviates(string typed, string word)
    {
        var letters = typed.EnumerateRunes().ToArray();
        var of = word.EnumerateRunes().ToArray();
        if (letters.Length == 0 || of.Length == 0 || of[0] != letters[0]) return false;
        var rest = 1;
        foreach (var letter in of.AsSpan(1))
            if (rest < letters.Length && letter == letters[rest]) rest++;
        return rest == letters.Length;
    }

    public static bool Contains(ReadOnlySpan<byte> text, ReadOnlySpan<byte> word) => text.IndexOf(word) >= 0;

    /// Whether `word` starts a word of `text`: "api" in "team/api", not "rapid".
    public static bool StartsWord(ReadOnlySpan<byte> word, ReadOnlySpan<byte> text)
    {
        if (word.IsEmpty || text.Length < word.Length) return false;
        var anywhere = !InWord(word[0]);
        var from = 0;
        while (from < text.Length)
        {
            var hit = text[from..].IndexOf(word);
            if (hit < 0) return false;
            var at = from + hit;
            if (anywhere || at == 0 || !InWord(text[at - 1])) return true;
            from = at + 1;
        }
        return false;
    }

    /// Letters and digits, and anything outside ASCII.
    private static bool InWord(byte b) => b >= 0x80 || b is >= 0x30 and <= 0x39 || b is >= 0x61 and <= 0x7A;

    public static bool Nearly(ReadOnlySpan<byte> typed, ReadOnlySpan<byte> text)
    {
        var allowed = typed.Length < 4 ? 0 : typed.Length < 8 ? 1 : 2;
        if (allowed == 0) return false;
        // Edit distance to the start of each word, where swapping two letters counts as one. Rows are reused across
        // words, and made at the first word worth measuring: most texts have none.
        var width = typed.Length + allowed + 1;
        int[] before = [], above = [], row = [];
        var start = 0;
        while (start < text.Length)
        {
            if (!InWord(text[start])) { start++; continue; }
            var end = start;
            while (end < text.Length && InWord(text[end])) end++;
            var word = text[start..end];
            start = end;
            // People rarely get the first letter wrong, so require it (or a swap with the second). This skips most
            // words cheaply.
            if (word.Length < 2 || word.Length < typed.Length - allowed || !(word[0] == typed[0] || (word[0] == typed[1] && word[1] == typed[0]))) continue;
            if (row.Length == 0) { before = new int[width]; above = new int[width]; row = new int[width]; }
            var columns = Math.Min(word.Length, width - 1);
            for (var j = 0; j <= columns; j++) above[j] = j;
            var far = false;
            for (var i = 1; i <= typed.Length; i++)
            {
                row[0] = i;
                var least = i;
                for (var j = 1; j <= columns; j++)
                {
                    var best = Math.Min(Math.Min(above[j] + 1, row[j - 1] + 1), above[j - 1] + (typed[i - 1] == word[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && typed[i - 1] == word[j - 2] && typed[i - 2] == word[j - 1]) best = Math.Min(best, before[j - 2] + 1);
                    row[j] = best;
                    least = Math.Min(least, best);
                }
                // Already too many typos.
                if (least > allowed) { far = true; break; }
                (before, above, row) = (above, row, before);
            }
            if (!far && above.AsSpan(1, columns).IndexOfAnyInRange(0, allowed) >= 0) return true;
        }
        return false;
    }
}
