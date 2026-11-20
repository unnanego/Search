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
        var bench = new ToggleSwitch { Header = "Let a script drive Search", IsOn = _prefs.Bench };
        var benchNote = new TextBlock { Text = $@"A script run as you on this PC can open tabs of its own and read them, through the pipe \\.\pipe\{Search.Bench.BenchServer.PipeFor(Store.Folder)}.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        bench.Toggled += (_, _) => _prefs.Bench = bench.IsOn;
        _settings = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Settings",
            CloseButtonText = "Done",
            RequestedTheme = Root.ActualTheme,
            Content = new ScrollViewer { Content = new StackPanel { Spacing = 16, MinWidth = 420, Children = { side, theme, engine, custom, bench, benchNote } } }
        };
        try { await _settings.ShowAsync(); }
        finally { _prefs.CustomEngine = custom.Text.Trim(); _settings = null; }
    }
}
