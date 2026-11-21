using Search.Core;
using Velopack;
using Velopack.Sources;

namespace Search.App;

// Knowing when there is a newer one, and having it ready (Updater.swift). Once a day, quietly: check, download, and
// leave it for the next launch, which Velopack applies before the window opens. Nothing restarts on its own. A build
// that wasn't installed (a dev build, a test run) never checks: there is nothing to update.
public static class Updater
{
    /// Where releases are published. A fork publishes its own; this is this fork's.
    public const string Releases = "https://github.com/unnanego/Search";

    private static readonly TimeSpan _every = TimeSpan.FromDays(1);
    private static string Stamp => Store.File("update-checked");

    /// What happened at the last check, for Settings: null until there's been one.
    public static string? Status { get; private set; }

    public static async Task CheckSoon()
    {
        if (Store.Testing) return;
        UpdateManager manager;
        try { manager = new UpdateManager(new GithubSource(Releases, null, prerelease: false)); } catch (Exception e) { Status = e.Message; return; }
        if (!manager.IsInstalled) { Status = "Not installed: this build doesn't update itself."; return; }
        if (File.Exists(Stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Stamp) < _every) return;
        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            File.WriteAllText(Stamp, "");
            if (await manager.CheckForUpdatesAsync() is not { } update) { Status = "Up to date."; return; }
            await manager.DownloadUpdatesAsync(update);
            Status = $"Version {update.TargetFullRelease.Version} is ready for the next time Search opens.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException) { Status = "Couldn't check: " + e.Message; }
    }
}
