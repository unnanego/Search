using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Search.Core;
using Search.Engine;

namespace Search.App;

// A window, a row of titles, and a field (App.swift). Typing an address gets you a page; everything else is a key
// away. The pieces live in partial files: the field, the keys, find, the bench, the session.
public sealed partial class MainWindow : Window
{
    private readonly Prefs _prefs;
    private readonly History _history;
    private readonly Browser _browser = new();
    private readonly RowMirror _row;
    private readonly Pages _pages;
    private bool _fullScreen;

    public MainWindow(Prefs prefs, History history)
    {
        _prefs = prefs;
        _history = history;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        _row = new RowMirror(_browser);
        _pages = new Pages(_browser, Stage) { Theme = prefs.Theme };
        _ = _pages.SetShield(prefs.Shield, prefs.ShieldPaused);
        Row.ItemsSource = _row.Items;
        Strip.ItemsSource = _row.Flat;
        PinGrid.ItemsSource = _row.Pins;
        PinGrid.SelectionChanged += OnRowSelectionChanged;
        _row.Pins.CollectionChanged += (_, _) => PinGrid.Visibility = _row.Pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Row.SelectionChanged += OnRowSelectionChanged;
        Strip.SelectionChanged += OnRowSelectionChanged;
        Row.DragItemsCompleted += (_, e) => _row.Reordered(e.Items.ToList());
        Strip.DragItemsCompleted += (_, e) => _row.Reordered(e.Items.ToList());
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) OnSelected(); };
        _pages.Visited += (_, url, title) => _history.Record(url, title);
        _pages.TitleChanged += (tab, title) => { if (tab == _browser.Selected) Title = Shown(tab); if (!tab.Bench && !tab.Private && tab.Url is { } url) _history.Retitle(url, title); };
        _pages.FullScreen += (tab, on) => { if (tab == _browser.Selected) SetFullScreen(on); };
        _prefs.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Prefs.TabsOnSide) or nameof(Prefs.Folded) or nameof(Prefs.ColumnWidth)) Lay(); if (e.PropertyName == nameof(Prefs.Theme)) Paint(); if (e.PropertyName == nameof(Prefs.Bench)) Bench(); if (e.PropertyName is nameof(Prefs.Shield) or nameof(Prefs.ShieldPaused)) _ = _pages.SetShield(_prefs.Shield, _prefs.ShieldPaused); };
        WireField();
        WireKeys();
        _pages.Keys = PageKeys();
        _pages.KeyPressed += (tab, combo) => { if (tab == _browser.Selected || tab.Peek && tab == _peek) Press(combo); };
        WireFind();
        WirePanels();
        WireSite();
        WirePasswords();
        WireBookmarks();
        WireCurtain();
        WireSplit();
        WireSwitcher();
        WirePeek();
        WireFold();
        WireIdle();
        WireRow();
        Lay();
        Paint();
        StartSpaces();
        Restore();
        _browser.SessionChanged += () => SaveSession(now: false);
        Bench();
        // On the way out, whatever is still on its way to the disk (notes, bookmarks, the vault) gets there first.
        Closed += (_, _) => { SaveSession(now: true); _history.Flush(); _bench?.Dispose(); Disk.Flush().Wait(TimeSpan.FromSeconds(3)); };
        if (Store.Testing && Environment.GetEnvironmentVariable("SEARCH_FOCUS_LOG") == "1")
        {
            Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} got {Describe(e.NewFocusedElement)}\n");
            Activated += (_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} window {e.WindowActivationState}\n");
            FieldText.LostFocus += (_, _) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} field lost\n");
            Root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} keydown {e.Key} from {Describe(e.OriginalSource)} handled={e.Handled}\n")), true);
            Root.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} previewkeydown {e.Key} from {Describe(e.OriginalSource)}\n")), true);
        }
    }

    private static string Describe(object? element) => element switch { FrameworkElement { Name.Length: > 0 } f => $"{f.GetType().Name} {f.Name}", null => "null", _ => element.GetType().Name };

    private readonly List<LittleWindow> _littles = new();

    /// A link from another app (Links.swift): a small window of its own (Little.swift), or a tab in front.
    public void FromElsewhere(Uri link)
    {
        // A route sends the link to its space, in the window, with that space's sign-ins.
        if (_spaces.UsesSpaces && Route(link)) { OpenHere(link); return; }
        if (_prefs.LittleLinks && link.Scheme is "http" or "https")
        {
            var little = new LittleWindow(this, link, _prefs);
            _littles.Add(little);
            little.Gone += gone => _littles.Remove(gone);
            little.Activate();
            return;
        }
        OpenHere(link);
    }

    /// A page to a tab of this window, in front: from another app, or kept from a small window.
    public void OpenHere(Uri link)
    {
        Bring();
        var tab = _browser.Selected is { Url: null, Private: false } empty ? empty : _browser.Open(link);
        if (tab.Url is null) tab.Go(link);
        _ = Front(tab, givePage: true);
    }

    /// The window to the front, as when Search is opened again while it's running.
    public void Bring()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private static string Shown(Tab tab) => tab.Private ? "Private — " + tab.Shown : tab.Shown;

    private void OnSelected()
    {
        var tab = _browser.Selected;
        Row.SelectedItem = tab is { Pinned: false } ? tab : null;
        PinGrid.SelectedItem = tab is { Pinned: true } ? tab : null;
        Strip.SelectedItem = tab;
        Title = tab is null ? "Search" : Shown(tab);
        ShowAddressNow();
        CloseFind();
        if (tab is null) { FocusField(); return; }
        _ = Front(tab);
    }

    /// The tab in front shown, and the keys given to its page (always after Enter in the field, otherwise unless the
    /// field has them); a tab with no address yet gives them to the field.
    private async Task Front(Tab tab, bool givePage = false)
    {
        if (tab.Url is null) { await _pages.Show(); FocusField(); return; }
        // A page can take a moment to be built; if you asked for the field or find in that moment, it keeps the keys.
        var asked = _keysAsked;
        await _pages.Show();
        if (_browser.Selected == tab && asked == _keysAsked && (givePage || FieldText.FocusState == FocusState.Unfocused)) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    /// Counts each time you ask for the keyboard somewhere (the field, find), so a page built late doesn't take it back.
    private int _keysAsked;

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListViewBase)sender;
        if (list.SelectedItem is Tab tab && tab != _browser.Selected) _browser.Selected = tab;
        // A group's heading folds or opens its group; the tab in front stays selected.
        else if (list.SelectedItem is GroupRow heading)
        {
            _browser.SetGroup(heading.Group with { Collapsed = !heading.Group.Collapsed });
            list.SelectedItem = _browser.Selected is { } front && _row.Items.Contains(front) ? front : null;
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: Tab tab }) Close(tab); }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => NewTab();

    private void NewTab(bool @private = false) { _browser.Open(null, @private: @private); FocusField(); }

    private void Close(Tab tab)
    {
        _browser.Close(tab);
        if (_browser.Ordinary.Any()) return;
        // The last tab closed leaves an empty one, ready for an address, rather than an empty window.
        NewTab();
    }

    /// Down the left or across the top, the column folded or not, and where the field sits in each.
    private void Lay()
    {
        var side = _prefs.TabsOnSide;
        var column = side && !_prefs.Folded && !_fullScreen;
        Column.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
        ColumnDef.Width = new GridLength(column ? _prefs.ColumnWidth : 0);
        Strip.Visibility = side || _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        TopRow.Height = new GridLength(_fullScreen ? 0 : 40);
        Card.Margin = _fullScreen ? new Thickness(0) : new Thickness(column ? 4 : 8, 0, 8, 8);
        Card.CornerRadius = new CornerRadius(_fullScreen ? 0 : 8);
        Grid.SetColumn(Card, column ? 1 : 0);
        Grid.SetColumnSpan(Card, column ? 1 : 2);
        Grid.SetColumn(FieldHost, column ? 1 : 0);
        Grid.SetColumnSpan(FieldHost, column ? 1 : 2);
        // In the column the field lives at its head; otherwise it floats over the page when called for.
        Detach(Field);
        if (column) ColumnFieldSlot.Child = Field; else FieldHost.Children.Add(Field);
        FieldHost.Visibility = Visibility.Collapsed;
        SetTitleBar(side ? TopBar : DragSpace);
        LayFold();
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent ?? VisualTreeHelper.GetParent(element))
        {
            case Border border: border.Child = null; break;
            case Panel panel: panel.Children.Remove(element); break;
        }
    }

    private bool FieldFloats => FieldHost.Children.Contains(Field);

    private void Paint()
    {
        Root.RequestedTheme = _prefs.Theme switch { Theme.Light => ElementTheme.Light, Theme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        Root.Background = (_spaces is null ? null : Wash()) ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _pages.Theme = _prefs.Theme;
    }

    private void SetFullScreen(bool on)
    {
        if (_fullScreen == on) return;
        _fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        Lay();
    }
}
