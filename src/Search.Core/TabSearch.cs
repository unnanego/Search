namespace Search.Core;

// Ctrl+T finds your open tabs (Browser.tabOffers, Settings › Ctrl+T finds your open tabs, off unless turned on): the
// open tabs, then the pins, that answer to what is typed, the best answer first and, between equals, the one looked at
// last. At most three of the one and two of the other, so the list stays short.
public static class TabSearch
{
    public const int OpenOffered = 3, PinsOffered = 2;

    /// How well what is typed names a tab: 3 when its name or address starts with it, 2 when every word typed starts a
    /// word of the name or a part of the address, 1 when every word is somewhere in them, 0 not at all. One letter
    /// counts only at the start of a word, or "e" would name every tab there is.
    public static int Relevance(string label, string address, string typed)
    {
        var whole = typed.Trim().ToLowerInvariant();
        var words = whole.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var name = label.ToLowerInvariant();
        var place = address.ToLowerInvariant();
        if (name.StartsWith(whole, StringComparison.Ordinal) || place.StartsWith(whole, StringComparison.Ordinal)) return 3;
        var text = name + " " + place;
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{N}]+");
        if (words.All(w => parts.Any(p => p.StartsWith(w, StringComparison.Ordinal)))) return 2;
        return whole.Length >= 2 && words.All(w => text.Contains(w, StringComparison.Ordinal)) ? 1 : 0;
    }

    /// The tabs that answer, ranked: (tab, label, address, pinned, touched) in; the chosen ones out, open first.
    public static List<T> Ranked<T>(IEnumerable<T> tabs, string typed, Func<T, string> label, Func<T, string> address, Func<T, bool> pinned, Func<T, DateTimeOffset?> touched)
    {
        var ranked = tabs.Select(t => (Tab: t, Score: Relevance(label(t), address(t), typed))).Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score).ThenByDescending(s => touched(s.Tab) ?? DateTimeOffset.MinValue).Select(s => s.Tab).ToList();
        return [.. ranked.Where(t => !pinned(t)).Take(OpenOffered), .. ranked.Where(pinned).Take(PinsOffered)];
    }

    /// The grey after the caret: the rest of the tab's address, only when what was typed starts it.
    public static string? Ending(string typed, string address)
    {
        var words = typed.Trim();
        return words.Length > 0 && words == typed && address.StartsWith(words, StringComparison.OrdinalIgnoreCase) && address.Length > words.Length ? address[words.Length..] : null;
    }
}
