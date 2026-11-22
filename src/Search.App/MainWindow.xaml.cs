using System.ComponentModel;
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

    public MainWindow(Shared shared, WindowRecord? record = null, bool primary = false, MainWindow? from = null, Guid? profile = null)
    {
        _prefs = shared.Prefs;
        // A window belongs to one profile for its whole life (Profiles.swift): the one it was opened for, the one of
        // the window it came from, or the one of the space it showed.
        var spaces = shared.Spaces;
        Profile = profile is { } p && shared.Profiles.Has(p) ? p : from?.Profile ?? spaces.ProfileOf(record?.Space ?? spaces.Start().Space);
        _history = shared.HistoryOf(Profile);
        _bookmarks = shared.BookmarksOf(Profile);
        _archive = shared.ArchiveOf(Profile);
        // The oldest window of its profile holds that profile's session files (WindowsFile.FileHolders).
        _owner = primary || App.Windows.All(w => w.Profile != Profile);
        _record = record;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();
        Place(record, from);
        _row = MakeRow();
        _pages = new Pages(_browser, Stage) { Theme = _prefs.Theme, TriesHttpsFirst = () => _prefs.TriesHttpsFirst };
        _ = _pages.SetShield(_prefs.Shield, _prefs.ShieldPaused);
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
        _pages.TitleChanged += (tab, title) => { if (tab == _browser.Selected) Title = Titled(tab); if (!tab.Bench && !tab.Private && tab.Url is { } url) _history.Retitle(url, title); };
        _pages.FullScreen += (tab, on) => { if (tab == _browser.Selected) SetFullScreen(on); };
        Hook<PropertyChangedEventHandler>(h => _prefs.PropertyChanged += h, h => _prefs.PropertyChanged -= h, (_, e) => { if (e.PropertyName is nameof(Prefs.TabsOnSide) or nameof(Prefs.Folded) or nameof(Prefs.ColumnWidth)) Lay(); if (e.PropertyName == nameof(Prefs.Theme)) Paint(); if (e.PropertyName is nameof(Prefs.Shield) or nameof(Prefs.ShieldPaused)) _ = _pages.SetShield(_prefs.Shield, _prefs.ShieldPaused); });
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
        WireFolders();
        WireCapture();
        WireSplit();
        WireSwitcher();
        WirePeek();
        WireFold();
        WireIdle();
        WireRow();
        WireTitleBar();
        WireAgent();
        WireExtensions();
        Lay();
        Paint();
        StartSpaces(record, from);
        WirePins();
        Restore();
        // Pins are told to the other windows at once; the row is written a moment later.
        _browser.SessionChanged += () => { TellPins(); SaveSession(now: false); };
        AppWindow.Changed += (_, e) => { if (e.DidPositionChange || e.DidSizeChange) App.SaveWindows(); };
        if (Store.Testing && Environment.GetEnvironmentVariable("SEARCH_FOCUS_LOG") == "1")
        {
            Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} got {Describe(e.NewFocusedElement)}\n");
            Activated += (_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} window {e.WindowActivationState}\n");
            FieldText.LostFocus += (_, _) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} field lost\n");
            Root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} keydown {e.Key} from {Describe(e.OriginalSource)} handled={e.Handled}\n")), true);
            Root.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => File.AppendAllText(Store.File("focus.log"), $"{DateTime.Now:HH:mm:ss.fff} previewkeydown {e.Key} from {Describe(e.OriginalSource)}\n")), true);
        }
    }

    /// The profile this window belongs to, for its whole life.
    public Guid Profile { get; }
    /// Whether it holds its profile's session files.
    public bool Owner => _owner;

    /// What this window was handed at launch (windows.json's line for it), until its rows are read.
    private readonly WindowRecord? _record;
    /// Whether this window holds the session files: the oldest window still open (Windows.swift's usesFiles).
    private bool _owner;
    /// Unhooking from what every window shares, when this one goes.
    private readonly List<Action> _unhook = new();

    private void Hook<T>(Action<T> on, Action<T> off, T handler) where T : Delegate { on(handler); _unhook.Add(() => off(handler)); }

    private static string Describe(object? element) => element switch { FrameworkElement { Name.Length: > 0 } f => $"{f.GetType().Name} {f.Name}", null => "null", _ => element.GetType().Name };

    private readonly List<LittleWindow> _littles = new();

    /// A link from another app (Links.swift): a small window of its own (Little.swift), or a tab in front.
    public void FromElsewhere(Uri link) => Land(link, _prefs.LittleLinks);

    /// A page asked for from outside the window (another app, the launcher): to its space's route, to a small window
    /// over the app you're in, or to a tab here.
    internal void Land(Uri link, bool small)
    {
        // A route sends the link to its space, in the window, with that space's sign-ins.
        if (_spaces.UsesSpaces && Route(link)) { OpenHere(link); return; }
        if (small && link.Scheme is "http" or "https")
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
        PinGrid.SelectedItem = tab is { Pinned: true } && !(tab.Listed && _prefs.ShowsPinRows) ? tab : null;
        // In a folded folder the tab in front is still drawn: the rows are worked out again.
        if (_prefs.ShowsPinFolders) _row.Sync();
        MarkPinRow();
        Strip.SelectedItem = tab;
        Title = Titled(tab);
        ShowAddressNow();
        CloseFind();
        DispatcherQueue.TryEnqueue(RevealSelected);
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

    private void NewTab(bool @private = false) { _browser.Open(null, @private: @private); FocusField(); _launching = true; }

    /// Ctrl+Shift+T: the tab closed last, or the window, whichever went later.
    private void Reopen()
    {
        if (App.LastClosedAt is { } window && (!_browser.CanReopen || window > _tabClosedAt) && App.ReopenWindow()) return;
        _browser.Reopen();
    }

    private DateTimeOffset _tabClosedAt;

    private void Close(Tab tab)
    {
        _tabClosedAt = DateTimeOffset.UtcNow;
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
        LayTitleBar();
        LayExtensions();
        LayFold();
    }

    /// Before the window is loaded an element has no Parent to be found by, so the slots the field and the extensions'
    /// buttons start in are asked too: launched with the tabs across the top, the field couldn't be moved out of the
    /// column and the window never opened.
    private void Detach(FrameworkElement element)
    {
        switch (element.Parent ?? VisualTreeHelper.GetParent(element) ?? new[] { ColumnFieldSlot, ColumnExtensionsSlot, TopExtensionsSlot }.FirstOrDefault(slot => slot.Child == element))
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
