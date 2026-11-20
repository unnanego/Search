using Search.Core;

namespace Search.Core.Tests;

/// Points Store at a temp folder before anything reads it (Store.Folder is worked out once), so no test ever
/// touches the real %LOCALAPPDATA%\Search. Every test class that uses files calls Ready() first.
internal static class TempStore
{
    static TempStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"search-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Store.Root = root;
    }

    public static void Ready() { }

    /// A file name of its own, so tests running side by side never share one.
    public static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}.json";
}
