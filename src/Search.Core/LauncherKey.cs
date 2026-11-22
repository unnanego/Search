namespace Search.Core;

// The launcher's rules, apart from any window (Launcher.swift, HotKey.swift): the key it is on, as Settings shows it
// and as RegisterHotKey takes it; which keys are Windows's own and never offered; when the key is the page's rather
// than the launcher's; and how the list under its field is put together and walked.

/// A key with its modifiers, as RegisterHotKey takes them (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8) and a
/// virtual-key code, written as Settings shows it: "Ctrl+Alt+Space".
public sealed record LauncherKey(int Modifiers, int Code)
{
    public const int Alt = 1, Control = 2, Shift = 4, Win = 8;

    /// What Settings offers in one click. The Mac's ⇧⌘Space would be Ctrl+Shift+Space here, which Office and Visual
    /// Studio use for their own; Ctrl+Alt+Space is nobody's.
    public static readonly LauncherKey Suggested = new(Control | Alt, 0x20);

    private static readonly (int Code, string Name)[] _named =
    [
        (0x20, "Space"), (0x0D, "Enter"), (0x08, "Backspace"), (0x09, "Tab"), (0x1B, "Esc"), (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"),
        (0x21, "PageUp"), (0x22, "PageDown"), (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"), (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'")
    ];

    public string Display => string.Join("+", Parts());

    private IEnumerable<string> Parts()
    {
        if ((Modifiers & Control) != 0) yield return "Ctrl";
        if ((Modifiers & Alt) != 0) yield return "Alt";
        if ((Modifiers & Shift) != 0) yield return "Shift";
        if ((Modifiers & Win) != 0) yield return "Win";
        yield return NameOf(Code);
    }

    public override string ToString() => Display;

    public static string NameOf(int code) => code switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)code).ToString(),
        >= 0x70 and <= 0x87 => "F" + (code - 0x6F),
        _ => _named.FirstOrDefault(n => n.Code == code).Name ?? $"Key{code}"
    };

    /// "Ctrl+Alt+Space" back into a key; null for anything that isn't one.
    public static LauncherKey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "Ctrl++" is Ctrl and the plus key, which has no name of its own here: not a key Settings makes.
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) return null;
        var modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch { "ctrl" or "control" => Control, "alt" => Alt, "shift" => Shift, "win" or "windows" => Win, _ => -1 };
            if (flag < 0 || (modifiers & flag) != 0) return null;
            modifiers |= flag;
        }
        return CodeOf(parts[^1]) is { } code ? new LauncherKey(modifiers, code) : null;
    }

    private static int? CodeOf(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var c and (>= 'A' and <= 'Z' or >= '0' and <= '9')) return c;
        if (name.Length is 2 or 3 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return 0x6F + f;
        if (name.StartsWith("Key", StringComparison.Ordinal) && int.TryParse(name[3..], out var raw) && raw is > 0 and < 0xFF) return raw;
        foreach (var (code, named) in _named) if (string.Equals(named, name, StringComparison.OrdinalIgnoreCase)) return code;
        return null;
    }

    /// Why this key can't be the launcher's, or null if it can. A key needs Ctrl, Alt or Win, or every app's typing
    /// would lose it; and those Windows keeps for itself never reach an app, or would break Windows if they did.
    public string? Refused()
    {
        if ((Modifiers & (Control | Alt | Win)) == 0) return $"{Display} needs Ctrl, Alt or Win with it";
        if (Code is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C) return "Choose a key to go with the modifiers";
        return UsedByWindows() ? $"{Display} is used by Windows" : null;
    }

    private bool UsedByWindows()
    {
        var m = Modifiers & ~Shift;
        // Win with a letter, a digit, a cursor key, Space, Tab or a comma or period: Windows's, now or in some edition.
        if ((Modifiers & Win) != 0 && (Code is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or 0x20 or 0x09 or >= 0x25 and <= 0x28 or 0xBC or 0xBE or 0x2E or 0xBB or 0xBD)) return true;
        return (m, Code) switch
        {
            (Alt, 0x09) or (Alt, 0x73) or (Alt, 0x1B) or (Alt, 0x20) => true,   // Alt+Tab, Alt+F4, Alt+Esc, the window menu
            (Control, 0x1B) => true,                                            // Ctrl+Esc and Ctrl+Shift+Esc
            (Control | Alt, 0x2E) or (Control | Alt, 0x09) => true,             // Ctrl+Alt+Delete, the sticky Alt+Tab
            _ => false
        };
    }
}

public static class LauncherRules
{
    /// How many open pages head the list: three, as the Mac's.
    public const int OpenOffered = 3;

    /// With a window of Search's in front, the key is the page's and Ctrl+L is the field over it; anywhere else (another
    /// app in front, Search's windows all minimised) it is the launcher's. Up, it stays the launcher's so that pressed
    /// again it puts the field away.
    public static bool StandsAside(bool searchInFront, bool windowShowing) => searchInFront && windowShowing;

    public static bool Wants(bool on, LauncherKey? key, bool recording, bool up, bool searchInFront, bool windowShowing) =>
        on && key is not null && !recording && (up || !StandsAside(searchInFront, windowShowing));

    /// The list: the pages already open first, then the field's places, less any that is a page already listed as open.
    public static List<Suggestion> Arrange(IEnumerable<Suggestion> open, IEnumerable<Suggestion> places)
    {
        var opened = open.Take(OpenOffered).ToList();
        var shown = opened.Select(o => History.Identity(o.Url)).ToHashSet();
        return opened.Concat(places.Where(p => p.Tab is not null || p.Kind == SuggestionKind.Search || !shown.Contains(History.Identity(p.Url)))).ToList();
    }

    /// The arrow keys walk the list, and walking off either end lets go (null: nothing picked, what was typed goes).
    public static int? Walk(int? picked, int step, int count) => count == 0 ? null : picked switch
    {
        null => step > 0 ? 0 : count - 1,
        { } here when here + step < 0 || here + step >= count => null,
        { } here => here + step
    };

    /// Where the launcher puts itself: centred across the screen with the pointer, its top this far down it.
    public const double Drop = 0.18;
}
