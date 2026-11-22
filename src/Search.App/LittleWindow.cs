using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Search.Core;
using Search.Engine;
using Windows.System;

namespace Search.App;

// A small window for a link from another app (Little.swift, Arc's Little Arc): the page, and a thin line over it
// with the site, Open in Search and nothing else, to read and close or to keep. It has a browser and pages of its own,
// one tab, so the page gets everything a tab in the window gets: Shield, Search's scripts, the keys.
public sealed partial class LittleWindow : Window
{
    private readonly MainWindow _main;
    private readonly Browser _browser = new();
    private readonly Pages _pages;
    private readonly Tab _tab;
    private readonly TextBlock _site = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };

    private bool _closing;
    public Tab Tab => _tab;
    public event Action<LittleWindow>? Gone;

    public LittleWindow(MainWindow main, Uri link, Prefs prefs)
    {
        _main = main;
        Title = "Search";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 760));
        var stage = new Grid();
        var keep = new Button { Content = "Open in Search", Margin = new Thickness(0, 0, 140, 0) };
        keep.Click += (_, _) => OpenInSearch();
        var bar = new Grid { Height = 36, Padding = new Thickness(12, 0, 0, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(keep, 1);
        bar.Children.Add(_site);
        bar.Children.Add(keep);
        var card = new Border { Child = stage, CornerRadius = new CornerRadius(8), Margin = new Thickness(8, 0, 8, 8), Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        Grid.SetRow(card, 1);
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Children = { bar, card } };
        root.RequestedTheme = prefs.Theme switch { Theme.Light => ElementTheme.Light, Theme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        Content = root;
        SetTitleBar(_site);
        _pages = new Pages(_browser, stage) { Theme = prefs.Theme, Keys = new[] { "ctrl+w", "ctrl+o", "f5", "ctrl+r" }, TriesHttpsFirst = () => prefs.TriesHttpsFirst };
        _pages.AskWhereToSave = suggested => MainWindow.AskWhere(this, suggested);
        _ = _pages.SetShield(prefs.Shield, prefs.ShieldPaused);
        _pages.KeyPressed += (_, combo) => Key(combo);
        _pages.TitleChanged += (_, _) => Line();
        Key(root, VirtualKey.W, () => Close());
        Key(root, VirtualKey.O, OpenInSearch);
        _tab = _browser.Open(link);
        _tab.PropertyChanged += (_, _) => Line();
        Line();
        // The page let go of while the window is still whole: closed after it, WebView2 and XAML were torn down out of
        // order and the app could fail inside WinUI's dispatcher.
        AppWindow.Closing += (_, _) => Let();
        Closed += (_, _) => { Let(); Gone?.Invoke(this); };
    }

    private void Let()
    {
        if (_closing) return;
        _closing = true;
        _pages.Sleep(_tab);
    }

    private void Line()
    {
        if (_closing) return;
        _site.Text = _tab.Url is { } url ? Search.Core.Address.WithoutWww(url.Host) + (_tab.Title.Length > 0 ? " · " + _tab.Title : "") : "";
        Title = _tab.Title.Length > 0 ? _tab.Title : "Search";
    }

    private void Key(string combo)
    {
        if (_closing) return;
        if (combo == "ctrl+w") Close();
        else if (combo == "ctrl+o") OpenInSearch();
        else if (combo is "f5" or "ctrl+r") _tab.Do(TabCommand.Reload);
    }

    private static void Key(UIElement root, VirtualKey key, Action action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
        root.KeyboardAccelerators.Add(accelerator);
    }

    /// Kept: the page to a tab of the window, and this one closes.
    private void OpenInSearch()
    {
        if (_closing) return;
        if (_tab.Url is { } url) _main.OpenHere(url);
        Close();
    }
}
