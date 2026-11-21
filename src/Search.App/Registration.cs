using Microsoft.Win32;
using Search.Core;

namespace Search.App;

// Search as a browser Windows knows about (Links.swift's other half): registered for the current user only, so it can
// be chosen in Settings › Apps › Default apps. Windows lets no app make itself the default; it can only be offered.
// Written by the installer, and again from Settings if it's missing; never by a test run, which must leave the PC as
// it found it.
public static class Registration
{
    private const string Name = "Search";
    private const string ProgId = "SearchHTML";
    private const string Capabilities = @"Software\Clients\StartMenuInternet\Search\Capabilities";

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("no executable path");

    public static bool Registered => Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications")?.GetValue(Name) is string;

    public static void Register()
    {
        if (Store.Testing) return;
        var open = $"\"{Exe}\" \"%1\"";
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Search Document");
            prog.SetValue("FriendlyTypeName", "Search Document");
            using var icon = prog.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{Exe}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command"); command.SetValue("", open);
        }
        using (var client = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\Search"))
        {
            client.SetValue("", Name);
            using var command = client.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{Exe}\"");
            using var icon = client.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{Exe}\",0");
        }
        using (var caps = Registry.CurrentUser.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", Name);
            caps.SetValue("ApplicationDescription", "A small, fast, quiet web browser.");
            caps.SetValue("ApplicationIcon", $"\"{Exe}\",0");
            using var urls = caps.CreateSubKey("URLAssociations");
            foreach (var scheme in new[] { "http", "https" }) urls.SetValue(scheme, ProgId);
            using var files = caps.CreateSubKey("FileAssociations");
            foreach (var extension in new[] { ".htm", ".html", ".shtml", ".xhtml", ".svg", ".webp" }) files.SetValue(extension, ProgId);
        }
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(Name, Capabilities);
    }

    public static void Unregister()
    {
        if (Store.Testing) return;
        Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true)?.DeleteValue(Name, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Clients\StartMenuInternet\Search", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
    }

    /// Windows' own page for choosing the default browser, opened on Search's entry.
    public static void OpenDefaultApps() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(Name)}"));
}
