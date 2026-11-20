namespace Search.Core;

// Where everything this browser keeps is kept (Store.swift).
//
// One place, and one rule: a run started for testing never touches the folder or the settings of the browser
// somebody is actually using. On Windows the settings are a file in that folder too, so keeping the folders
// apart keeps everything apart, WebView2's own user data included.
public static class Store
{
    /// Overridable for tests; %LOCALAPPDATA% otherwise (local, not roaming: caches and cookies don't belong on a roaming profile).
    public static string Root { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly Lazy<string> _folder = new(() => Path.Combine(Root, World is { } w ? $"Search ({w})" : "Search"));

    /// A test run if it says so (SEARCH_PROBE), or if it runs straight out of a build folder rather than an
    /// installed copy. The second half isn't belt and braces: on the Mac a dev build launched from a terminal
    /// once wrote over somebody's real session.
    public static bool Testing => IsTesting(Environment.GetEnvironmentVariable("SEARCH_PROBE"), Environment.ProcessPath);

    /// Which test world a test run lives in: "test" for SEARCH_PROBE=1 or a build-folder run, a name of its own
    /// otherwise. Null for the browser somebody is using.
    public static string? World => WorldFor(Environment.GetEnvironmentVariable("SEARCH_PROBE"), Environment.ProcessPath);

    /// A test run there to be weighed and timed rather than driven (SEARCH_MEASURE beside SEARCH_PROBE).
    public static bool Measuring => Testing && Environment.GetEnvironmentVariable("SEARCH_MEASURE") is not null;

    public static string Folder => _folder.Value;
    public static string File(string name) => Path.Combine(Folder, name);
    /// WebView2's user data folder: cookies, caches, extensions. One per world, never shared with the real one.
    public static string WebData => File("WebView2");

    internal static bool IsTesting(string? probe, string? exe) =>
        probe is not null || (exe is not null && exe.Replace('/', '\\').Contains(@"\bin\", StringComparison.OrdinalIgnoreCase));

    internal static string? WorldFor(string? probe, string? exe)
    {
        if (!IsTesting(probe, exe)) return null;
        var asked = new string((probe ?? "").ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return asked is "" or "1" or "test" ? "test" : asked;
    }

    /// A file that didn't decode is set aside rather than overwritten by the next save: bookmarks, history and a
    /// session aren't worth losing to a bad read. Failing to move it is fine; the read came back empty anyway.
    public static void Quarantine(string file)
    {
        if (!System.IO.File.Exists(file)) return;
        var aside = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
        try { System.IO.File.Move(file, aside); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
