using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Search.Engine;

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
        var findsTabs = new ToggleSwitch { Header = "Ctrl+T finds your open tabs", IsOn = _prefs.FindsOpenTabs };
        findsTabs.Toggled += (_, _) => _prefs.FindsOpenTabs = findsTabs.IsOn;
        var findsNote = new TextBlock { Text = "As you type in a new tab, an open tab or a pin that matches is picked, so Enter switches to a page you have instead of opening it again. ↑ lets go of it.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var shield = new ToggleSwitch { Header = "Block trackers and ads", IsOn = _prefs.Shield };
        shield.Toggled += (_, _) => _prefs.Shield = shield.IsOn;
        var paused = new TextBlock { Text = _prefs.ShieldPaused.Count == 0 ? "On for every site." : "Off on " + string.Join(", ", _prefs.ShieldPaused) + ". Turn it back on for a site from its site card.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var bookmarks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var import = new Button { Content = "Import bookmarks…" };
        import.Click += (_, _) => _ = ImportBookmarks();
        var export = new Button { Content = "Export bookmarks…" };
        export.Click += (_, _) => _ = ExportBookmarks();
        var bringIn = new Button { Content = "Bring in from another browser…" };
        bringIn.Click += (_, _) => { _settings?.Hide(); _ = OpenImport(); };
        bookmarks.Children.Add(bringIn);
        bookmarks.Children.Add(import);
        bookmarks.Children.Add(export);
        var spaces = new ToggleSwitch { Header = "Spaces", IsOn = _spaces.UsesSpaces };
        spaces.Toggled += (_, _) => { if (!spaces.IsOn && _space != _spaces.Home(_spaces.ProfileOf(_space))) GoToSpace(_spaces.Home(_spaces.ProfileOf(_space))); _spaces.UsesSpaces = spaces.IsOn; DrawSpaces(); };
        var routes = new ToggleSwitch { Header = "Send links from other apps to their space", IsOn = _routes.On };
        routes.Toggled += (_, _) => _routes.On = routes.IsOn;
        var pinRows = new ToggleSwitch { Header = "Pinned rows", IsOn = _prefs.PinRows };
        var pinFolders = new ToggleSwitch { Header = "Folders in pinned rows", IsOn = _prefs.PinFolders, IsEnabled = _prefs.PinRows };
        pinRows.Toggled += (_, _) => { _prefs.PinRows = pinRows.IsOn; pinFolders.IsEnabled = pinRows.IsOn; };
        pinFolders.Toggled += (_, _) => _prefs.PinFolders = pinFolders.IsOn;
        var pinNote = new TextBlock { Text = "Pins kept as rows under the squares, as in Arc, with folders of them and live folders filled from a site's feed. Right-click a pin for Show as Row.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var sleep = new ToggleSwitch { Header = "Put tabs you aren't using to sleep", IsOn = SleepOn };
        sleep.Toggled += (_, _) => SleepOn = sleep.IsOn;
        var lives = new[] { TabLife.Never, TabLife.HalfDay, TabLife.Day, TabLife.Week, TabLife.Month };
        var closing = new ComboBox { Header = "Close tabs left alone after", ItemsSource = lives.Select(l => Closing.Title(l)).ToList(), SelectedIndex = Array.IndexOf(lives, TabLife), MinWidth = 240 };
        closing.SelectionChanged += (_, _) => TabLife = lives[closing.SelectedIndex];
        var https = new ToggleSwitch { Header = "Try HTTPS first", IsOn = _prefs.TriesHttpsFirst };
        https.Toggled += (_, _) => _prefs.TriesHttpsFirst = https.IsOn;
        var httpsNote = new TextBlock { Text = "An http:// page loads over HTTPS where the site has it. Where it hasn't, the page loads as asked. Local addresses (localhost, .test, .local, IP addresses) are left as they are.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var styles = new ToggleSwitch { Header = "Your own style for a site", IsOn = _prefs.SiteStyles };
        styles.Toggled += (_, _) => _prefs.SiteStyles = styles.IsOn;
        var capture = new ToggleSwitch { Header = "Capture pages", IsOn = _prefs.Capture };
        capture.Toggled += (_, _) => _prefs.Capture = capture.IsOn;
        var captureNote = new TextBlock { Text = "Ctrl+Shift+X copies an area, an element or the whole page, and saves it to your downloads.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var passwords = new ToggleSwitch { Header = "Offer to save passwords", IsOn = _prefs.SavePasswords };
        passwords.Toggled += (_, _) => _prefs.SavePasswords = passwords.IsOn;
        var little = new ToggleSwitch { Header = "Links from other apps open in a small window", IsOn = _prefs.LittleLinks };
        little.Toggled += (_, _) => _prefs.LittleLinks = little.IsOn;
        var extensions = new Button { Content = "Extensions…" };
        extensions.Click += (_, _) => { _settings?.Hide(); OpenPanel(PanelKind.Extensions); };
        var browser = new Button { Content = "Make Search your default browser…" };
        browser.Click += (_, _) => { Registration.Register(); Registration.OpenDefaultApps(); };
        var updates = new TextBlock { Text = Updater.Status ?? "Search checks for a newer version once a day and has it ready for the next launch.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var bench = new ToggleSwitch { Header = "Let a script drive Search", IsOn = _prefs.Bench };
        var benchNote = new TextBlock { Text = $@"A script run as you on this PC can open tabs of its own and read them, through the pipe \\.\pipe\{Search.Bench.BenchServer.PipeFor(Store.Folder)}.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        bench.Toggled += (_, _) => _prefs.Bench = bench.IsOn;
        var agents = new ToggleSwitch { Header = "Let coding agents use Search", IsOn = _prefs.Agents };
        var agentsNote = new TextBlock { Text = $@"Claude Code, Codex and others, through search-mcp, after you allow each one here. They work in tabs of their own and never see passwords, private tabs or your other windows. Pipe: \\.\pipe\{AgentServer.PipeFor(Store.Folder)}.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        agents.Toggled += (_, _) => _prefs.Agents = agents.IsOn;
        _settings = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            CloseButtonText = "Done",
            RequestedTheme = Root.ActualTheme,
            Content = new ScrollViewer { Content = new StackPanel { Spacing = 16, MinWidth = 420, Children = { side, theme, engine, custom, findsTabs, findsNote, spaces, routes, pinRows, pinFolders, pinNote, ProfileSettings(), SyncSettings(), sleep, closing, shield, paused, https, httpsNote, styles, capture, captureNote, bookmarks, passwords, extensions, little, LauncherSettings(), browser, updates, bench, benchNote, agents, agentsNote } } }
        };
        try { await _settings.ShowAsync(); }
        finally { _prefs.CustomEngine = custom.Text.Trim(); _settings = null; }
    }
}
