using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Search.Core;
using Velopack;

namespace Search.App;

// Where the app starts, before any window: the installer's hooks, then one Search per world. A link clicked in
// another app starts Search with the address; if one is already running, the address is handed to it and this one
// leaves without a window.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // First of all, as Velopack asks: install, update and uninstall run their hooks here and return.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Registration.Register())
            .OnAfterUpdateFastCallback(_ => Registration.Register())
            .OnBeforeUninstallFastCallback(_ => Registration.Unregister())
            .Run();
        var key = Store.World is { } world ? "Search-" + world : "Search";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }
        instance.Activated += (_, e) => App.Redirected(e);
        Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// The addresses in a command line: links (http, https, file) and files on this PC, quoted or not.
    public static IEnumerable<Uri> Links(IEnumerable<string> arguments)
    {
        foreach (var raw in arguments)
        {
            var argument = raw.Trim().Trim('"');
            if (argument.Length == 0 || argument.StartsWith('-')) continue;
            if (Uri.TryCreate(argument, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "file") yield return url;
            else if (File.Exists(argument)) yield return new Uri(Path.GetFullPath(argument));
        }
    }

    /// A command line split as Windows does, for an activation that arrives as one string.
    public static string[] Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) { if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }
}
