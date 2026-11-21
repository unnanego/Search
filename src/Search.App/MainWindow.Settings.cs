using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;

namespace Search.App;

// Settings, the few Phase 1 has (Settings.swift is the full set): where the tabs go, light or dark, the search
// engine, and letting a script drive Search. Each takes effect as it's changed.
public sealed partial class MainWindow
{
    private ContentDialog? _settings;

    private async Task OpenSettings()
    {
        if (_settings is not null) return;
        var side = new ToggleSwitch { Header = "Tabs down the left", IsOn = _prefs.TabsOnSide, OnContent = "On the left", OffContent = "Across the top" };
        side.Toggled += (_, _) => _prefs.TabsOnSide = side.IsOn;
        var theme = new ComboBox { Header = "Appearance", ItemsSource = new[] { "Follow Windows", "Light", "Dark" }, SelectedIndex = (int)_prefs.Theme, MinWidth = 240 };
        theme.SelectionChanged += (_, _) => _prefs.Theme = (Theme)theme.SelectedIndex;
        var engines = SearchChoices();
        var engine = new ComboBox { Header = "Search engine", ItemsSource = engines.Select(e => e.Title).ToList(), SelectedIndex = Math.Max(0, engines.FindIndex(e => e.Id == _prefs.Engine)), MinWidth = 240 };
        var custom = new TextBox { Header = "Custom search address, with %s for the words", Text = _prefs.CustomEngine, PlaceholderText = "https://example.com/search?q=%s" };
        custom.Visibility = _prefs.Engine == "custom" ? Visibility.Visible : Visibility.Collapsed;
        engine.SelectionChanged += (_, _) => { _prefs.Engine = engines[engine.SelectedIndex].Id; custom.Visibility = _prefs.Engine == "custom" ? Visibility.Visible : Visibility.Collapsed; };
        custom.LostFocus += (_, _) => _prefs.CustomEngine = custom.Text.Trim();
        var shield = new ToggleSwitch { Header = "Block trackers and ads", IsOn = _prefs.Shield };
        shield.Toggled += (_, _) => _prefs.Shield = shield.IsOn;
        var paused = new TextBlock { Text = _prefs.ShieldPaused.Count == 0 ? "On for every site." : "Off on " + string.Join(", ", _prefs.ShieldPaused) + ". Turn it back on for a site from its site card.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var bookmarks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var import = new Button { Content = "Import bookmarks…" };
        import.Click += (_, _) => _ = ImportBookmarks();
        var export = new Button { Content = "Export bookmarks…" };
        export.Click += (_, _) => _ = ExportBookmarks();
        bookmarks.Children.Add(import);
        bookmarks.Children.Add(export);
        var styles = new ToggleSwitch { Header = "Your own style for a site", IsOn = _prefs.SiteStyles };
        styles.Toggled += (_, _) => _prefs.SiteStyles = styles.IsOn;
        var passwords = new ToggleSwitch { Header = "Offer to save passwords", IsOn = _prefs.SavePasswords };
        passwords.Toggled += (_, _) => _prefs.SavePasswords = passwords.IsOn;
        var browser = new Button { Content = "Make Search your default browser…" };
        browser.Click += (_, _) => { Registration.Register(); Registration.OpenDefaultApps(); };
        var updates = new TextBlock { Text = Updater.Status ?? "Search checks for a newer version once a day and has it ready for the next launch.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var bench = new ToggleSwitch { Header = "Let a script drive Search", IsOn = _prefs.Bench };
        var benchNote = new TextBlock { Text = $@"A script run as you on this PC can open tabs of its own and read them, through the pipe \\.\pipe\{Search.Bench.BenchServer.PipeFor(Store.Folder)}.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        bench.Toggled += (_, _) => _prefs.Bench = bench.IsOn;
        _settings = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            CloseButtonText = "Done",
            RequestedTheme = Root.ActualTheme,
            Content = new ScrollViewer { Content = new StackPanel { Spacing = 16, MinWidth = 420, Children = { side, theme, engine, custom, shield, paused, styles, bookmarks, passwords, browser, updates, bench, benchNote } } }
        };
        try { await _settings.ShowAsync(); }
        finally { _prefs.CustomEngine = custom.Text.Trim(); _settings = null; }
    }
}
