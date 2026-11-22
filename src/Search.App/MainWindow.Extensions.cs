using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Search.Engine;

namespace Search.App;

// Extensions in the window (ExtensionsUI.swift, ExtensionPopup.swift, SidePanel.swift, StoreRelay.swift): the pinned
// ones' buttons and the puzzle that lists them all at the foot of the column (beside the tabs when they run across the
// top), a popup hanging from its button and sized to its page, the side panel beside the page, "Add to Search" on the
// Chrome Web Store, and the panel that lists what's installed. A press is worked out by the extension itself, in a
// page of its own that Search loads first (the bridge): its popup for this tab, its side panel, or its onClicked.
public sealed partial class MainWindow
{
    private ExtensionHost _extensions = null!;
    private (string Id, WebView2 View)? _popup;
    private FrameworkElement? _popupAnchor;
    private (string Id, WebView2 View, string? Store)? _side;
    private Button? _puzzle;
    private readonly Dictionary<string, Button> _extensionButtons = new();
    /// The windows that are Search's own surfaces (popups, side panels), which extensions are told to leave out.
    private readonly HashSet<long> _surfaceWindows = new();
    /// The install question on screen, which the bench can answer in a test run.
    private TaskCompletionSource<bool>? _confirming;
    private readonly StackPanel _topEnd = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

    /// Updates are looked for by one window for the app: the first one made.
    private static bool _updatesWatched;
    private const string SideWidthKey = "sidepanel.width";
    public const string WebStore = "https://chromewebstore.google.com/category/extensions";

    private void WireExtensions()
    {
        _extensions = App.Shared.Extensions;
        _pages.Extensions = _extensions.Ready;
        Hook<Action>(h => _extensions.Changed += h, h => _extensions.Changed -= h, () => DispatcherQueue.TryEnqueue(() => { DrawExtensions(); if (_panel == PanelKind.Extensions) FillPanel(); TellStores(); }));
        // A tab opened from a popup that's up takes it away, as Chrome's do; one opened while the bridge works out a
        // press (an onClicked opening a tab) leaves it to finish.
        _pages.SurfaceOpened += _ => { if (PopupShown) ClosePopup(); };
        _pages.Message += (tab, kind, body) => { if (kind == "store") OnStore(tab, body); };
        _pages.Loaded += tab => { if (StoreIdOf(tab.Url) is not null) TellStore(tab); if (tab == _browser.Selected) _ = FollowFront(); };
        _pages.AddScript(Scripts.Read("store-relay.js").Replace("__SEARCH_STORE_HOSTS__", JsonSerializer.Serialize(StoreHosts)));
        _pages.AddPageScript(Scripts.Read("store-page.js").Replace("__SEARCH_STORE_HOSTS__", JsonSerializer.Serialize(StoreHosts)));
        PopupLayer.PointerPressed += (_, e) => { if (ReferenceEquals(e.OriginalSource, PopupLayer)) ClosePopup(); };
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) { if (PopupShown) ClosePopup(); _ = FollowFront(); } };
        WireSideHandle();
        DrawExtensions();
        // Once a day at most, a while after launch; never by itself in a test run.
        if (!Store.Testing && !_updatesWatched)
        {
            _updatesWatched = true;
            var later = DispatcherQueue.CreateTimer();
            later.Interval = TimeSpan.FromSeconds(30);
            later.Tick += (_, _) => { later.Interval = TimeSpan.FromHours(1); _ = _extensions.CheckForUpdates(m => (App.Acting ?? this).ConfirmInstall(m, updating: true)); };
            later.Start();
        }
    }

    // MARK: the buttons

    private void DrawExtensions()
    {
        ExtensionsBar.Children.Clear();
        _extensionButtons.Clear();
        var items = _extensions.List.Items;
        ExtensionsBar.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items.Where(i => i.Pinned == true))
        {
            var button = Squared(Face(item, 16), item.Name);
            button.Opacity = item.Enabled ? 1 : 0.4;
            var id = item.Id;
            button.Click += (_, _) => _ = Press(id, button);
            button.ContextFlyout = ExtensionMenu(item);
            _extensionButtons[id] = button;
            ExtensionsBar.Children.Add(button);
        }
        _puzzle = Squared(new FontIcon { Glyph = "", FontSize = 14 }, "Extensions");
        _puzzle.Click += (_, _) => ShowPuzzle();
        ExtensionsBar.Children.Add(_puzzle);
    }

    private static Button Squared(UIElement content, string name)
    {
        var button = new Button { Content = content, Width = 30, Height = 30, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    /// Its icon, or its initial rather than a puzzle piece that would pass for the button the list opens from.
    private UIElement Face(Installed item, int size)
    {
        if (_extensions.IconFile(item.Id, size * 2) is { } file) return new Image { Width = size, Height = size, Source = new BitmapImage(new Uri(file)) };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"],
            Child = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = size * 0.62, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    /// The puzzle's list: each extension to press, pin or unpin; right-click for the rest.
    private void ShowPuzzle()
    {
        if (_puzzle is null) return;
        var flyout = new Flyout();
        var list = new StackPanel { Spacing = 2, MinWidth = 260 };
        foreach (var item in _extensions.List.Items)
        {
            var id = item.Id;
            var row = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            var press = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), IsEnabled = item.Enabled,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Face(item, 16), new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 } } }
            };
            press.Click += (_, _) => { flyout.Hide(); _ = Press(id, _extensionButtons.GetValueOrDefault(id) ?? _puzzle); };
            var pin = Squared(new FontIcon { Glyph = item.Pinned == true ? "" : "", FontSize = 12 }, item.Pinned == true ? "Unpin" : "Pin");
            pin.Click += (_, _) => { flyout.Hide(); _extensions.SetPinned(id, item.Pinned != true); };
            Grid.SetColumn(pin, 1);
            row.Children.Add(press);
            row.Children.Add(pin);
            row.ContextFlyout = ExtensionMenu(item);
            list.Children.Add(row);
        }
        var manage = new HyperlinkButton { Content = "Manage extensions" };
        manage.Click += (_, _) => { flyout.Hide(); OpenPanel(PanelKind.Extensions); };
        list.Children.Add(manage);
        flyout.Content = list;
        flyout.ShowAt(_puzzle, new FlyoutShowOptions { Placement = _prefs.TabsOnSide ? FlyoutPlacementMode.TopEdgeAlignedLeft : FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    /// What a right-click on an extension offers (ExtensionActions in Extensions.swift).
    private MenuFlyout ExtensionMenu(Installed item)
    {
        var menu = new MenuFlyout();
        var id = item.Id;
        void Item(string text, Action action, bool enabled = true) { var i = new MenuFlyoutItem { Text = text, IsEnabled = enabled }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        var manifest = _extensions.ManifestOf(id);
        Item(item.Pinned == true ? "Unpin" : "Pin", () => _extensions.SetPinned(id, item.Pinned != true));
        if (manifest?.SidePanel is not null) Item("Open side panel", () => _ = OpenSide(id), item.Enabled);
        if (manifest?.Options is { } options) Item("Options", () => Go(new Uri(ExtensionHost.PageUrl(id, options)), newTab: true), item.Enabled);
        var sites = new MenuFlyoutSubItem { Text = "Runs on" };
        var access = _extensions.SitesOf(id);
        var all = new RadioMenuFlyoutItem { Text = "All the sites it asks for", IsChecked = access.Mode == SiteMode.All, GroupName = "sites" + id };
        all.Click += (_, _) => _ = _extensions.SetSites(id, access with { Mode = SiteMode.All });
        var some = new RadioMenuFlyoutItem { Text = access.Mode == SiteMode.Some && access.Hosts.Count > 0 ? "Only " + string.Join(", ", access.Hosts.Take(3)) + (access.Hosts.Count > 3 ? "…" : "") : "Only sites you name…", IsChecked = access.Mode == SiteMode.Some, GroupName = "sites" + id };
        some.Click += (_, _) => _ = NameSites(item);
        sites.Items.Add(all);
        sites.Items.Add(some);
        menu.Items.Add(sites);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!item.FromStore) Item("Reload", () => _ = Reload(id));
        Item(item.Enabled ? "Turn off" : "Turn on", () => { if (item.Enabled) { if (_popup?.Id == id) ClosePopup(); if (_side?.Id == id) CloseSide(); } _ = _extensions.SetEnabled(id, !item.Enabled); });
        Item("Remove…", () => _ = Remove(item));
        return menu;
    }

    private async Task NameSites(Installed item)
    {
        var access = _extensions.SitesOf(item.Id);
        var box = new TextBox { Text = string.Join(", ", access.Hosts), PlaceholderText = "example.com, news.example.org", AcceptsReturn = false };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Where {item.Name} runs", Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Only on these sites, each with its subdomains. Elsewhere its scripts aren't put in the page at all.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, box } }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var hosts = box.Text.Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(SiteAccess.Host).OfType<string>().Distinct().ToList();
        await _extensions.SetSites(item.Id, hosts.Count == 0 ? access with { Mode = SiteMode.All } : access with { Mode = SiteMode.Some, Hosts = hosts });
    }

    private async Task Reload(string id)
    {
        try { await _extensions.Reload(id); Notify($"Reloaded {_extensions.List.Find(id)?.Name}"); } catch (Exception e) when (e is CrxRefused or IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Notify(e.Message); }
    }

    private async Task Remove(Installed item)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Remove {item.Name}?", Content = "What it kept in this browser is forgotten with it.", PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_popup?.Id == item.Id) ClosePopup();
        if (_side?.Id == item.Id) CloseSide();
        await _extensions.Remove(item.Id);
    }

    // MARK: installing

    /// "Add “name” to Search?", with what it will be able to do. One question at a time; the bench answers it in a test.
    private async Task<bool> ConfirmInstall(Manifest manifest, bool updating = false)
    {
        var wants = manifest.Describe();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = updating ? $"An update to {manifest.Name} asks for more" : $"Add “{manifest.Name}” to Search?",
            Content = new TextBlock { Text = wants.Count == 0 ? "It doesn't ask for anything special." : "It will be able to:\n• " + string.Join("\n• ", wants), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = updating ? "Update" : "Add extension", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme
        };
        var answered = _confirming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var shown = dialog.ShowAsync().AsTask();
            var first = await Task.WhenAny(shown, answered.Task);
            if (first == answered.Task) { dialog.Hide(); return answered.Task.Result; }
            return await shown == ContentDialogResult.Primary;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { Notify("Close the dialog that's open, then try again."); return false; }
        finally { if (_confirming == answered) _confirming = null; }
    }

    private async Task AddFromStore(string id, bool ask = true)
    {
        try { if (await _extensions.InstallFromStore(id, m => ask ? ConfirmInstall(m) : Task.FromResult(true)) is { } item) Notify($"Added {item.Name}"); }
        catch (CrxRefused e) { Notify(e.Message); }
        catch (HttpRequestException) { Notify("The Chrome Web Store couldn't be reached."); }
        catch (TaskCanceledException) { Notify("The Chrome Web Store took too long to answer."); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Notify("The extension couldn't be put in place: " + e.Message); }
    }

    private async Task AddFolder(string? folder = null, bool ask = true)
    {
        if (folder is null)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is not { } picked) return;
            folder = picked.Path;
        }
        try { if (await _extensions.InstallFolder(folder, m => ask ? ConfirmInstall(m) : Task.FromResult(true)) is { } item) Notify($"Added {item.Name}"); }
        catch (Exception e) when (e is CrxRefused or IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Notify(e.Message); }
    }

    // MARK: the Chrome Web Store

    private static string[] StoreHosts => Store.Testing && Environment.GetEnvironmentVariable("SEARCH_STORE_HOSTS") is { Length: > 0 } extra ? ["chromewebstore.google.com", .. extra.Split(',')] : ["chromewebstore.google.com"];

    /// The extension a store page is about, from the tab's own address only.
    private static string? StoreIdOf(Uri? url)
    {
        if (url is null) return null;
        if (Crx.StoreId(url) is { } id) return id;
        if (!Store.Testing || !StoreHosts.Contains(url.Host)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url.AbsolutePath, "/detail/(?:[^/]+/)?([a-p]{32})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private void OnStore(Tab tab, JsonElement body)
    {
        if (body.TryGetProperty("placed", out _)) { TellStore(tab); return; }
        if (StoreIdOf(tab.Url) is not { } id) return;
        if (body.TryGetProperty("add", out _) && _extensions.List.Find(id) is null) _ = AddFromStore(id);
        if (body.TryGetProperty("remove", out _) && _extensions.List.Find(id) is { } item) _ = Remove(item);
    }

    /// A store page told what is installed and what is on its way, so its button can say "Added to Search" or "Adding…".
    private void TellStore(Tab tab) => _ = _pages.Evaluate(tab, $"window.__searchStore && window.__searchStore.state({JsonSerializer.Serialize(new { installed = _extensions.List.Items.Select(i => i.Id), busy = _extensions.Busy })})");
    private void TellStores() { foreach (var tab in _browser.Tabs.Where(t => t.Live && StoreIdOf(t.Url) is not null)) TellStore(tab); }

    // MARK: pressing a button, and the popup

    /// The profile of the tab in front, whose extensions a press is for. A private tab has none; its space's is used.
    private string? ExtensionStore => _browser.Selected is { Private: false } tab ? tab.Store : _browser.Store;
    /// The window of the tab in front, as extensions number it. A page still being built is waited for: it's on screen.
    private async Task<long?> FrontWindow()
    {
        if (_browser.Selected is not { Private: false, Url: not null } tab) return null;
        await _pages.Ensure(tab);
        return await _pages.WindowIdOf(tab);
    }

    /// A popup on screen, not the bridge working out a press in the same web view, unseen.
    private bool PopupShown => _popup is not null && PopupCard.Opacity > 0;

    private async Task<JsonObject?> Press(string id, FrameworkElement? anchor)
    {
        if (_popup is { } up && up.Id == id) { ClosePopup(); return null; }
        ClosePopup();
        if (_extensions.List.Find(id) is not { Enabled: true }) return null;
        PopupCard.Opacity = 0;
        PopupLayer.Visibility = Visibility.Visible;
        _popupAnchor = anchor;
        var view = await _pages.Surface(PopupStage, ExtensionStore, ExtensionHost.PageShim("popup", await FrontWindow(), _surfaceWindows));
        _popup = (id, view);
        var core = view.CoreWebView2;
        core.WindowCloseRequested += (_, _) => { if (_popup?.View == view) ClosePopup(); };
        core.WebMessageReceived += (_, e) => { if (_popup?.View == view) Sized(e.WebMessageAsJson); };
        if (await Pages.WindowIdOf(core) is { } own) _surfaceWindows.Add(own);
        await Load(core, ExtensionHost.BridgeUrl(id));
        var answer = await Run(core, ExtensionHost.Press);
        if (_popup?.View != view) return answer;
        if (answer?["popup"]?.GetValue<string>() is { } popup)
        {
            Place(25, 25);
            await Load(core, popup);
            view.Opacity = 1;
            view.IsHitTestVisible = true;
            PopupCard.Opacity = 1;
            view.Focus(FocusState.Programmatic);
            return answer;
        }
        ClosePopup();
        if (answer?["panel"]?.GetValue<string>() is { } panel) await OpenSide(id, panel);
        return answer;
    }

    /// Chrome sizes a popup to its content, between 25 and 800 wide and up to 600 tall.
    private void Sized(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["size"] is not JsonArray { Count: 2 } size) return;
            Place(Math.Clamp(size[0]!.GetValue<double>(), 25, 800), Math.Clamp(size[1]!.GetValue<double>(), 25, 600));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
    }

    /// Hanging from its button, kept inside the window: above a button at the foot of the column, below one up top.
    private void Place(double width, double height)
    {
        PopupCard.Width = width + 2;
        PopupCard.Height = height + 2;
        double x = 80, y = 48;
        if (_popupAnchor is { XamlRoot: not null } anchor)
        {
            var at = anchor.TransformToVisual(PopupLayer).TransformPoint(new Windows.Foundation.Point(0, 0));
            var below = at.Y < PopupLayer.ActualHeight / 2;
            x = at.X;
            y = below ? at.Y + anchor.ActualHeight + 4 : at.Y - PopupCard.Height - 4;
        }
        x = Math.Clamp(x, 8, Math.Max(8, PopupLayer.ActualWidth - PopupCard.Width - 8));
        y = Math.Clamp(y, 8, Math.Max(8, PopupLayer.ActualHeight - PopupCard.Height - 8));
        PopupCard.Margin = new Thickness(x, y, 0, 0);
    }

    private void ClosePopup()
    {
        if (_popup is not { } up) { PopupLayer.Visibility = Visibility.Collapsed; return; }
        _popup = null;
        PopupStage.Children.Remove(up.View);
        up.View.Close();
        PopupLayer.Visibility = Visibility.Collapsed;
        if (_browser.Selected is { Url: not null } tab && FocusManager.GetFocusedElement(Root.XamlRoot) is null or WebView2) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    private static async Task Load(CoreWebView2 core, string url)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e) { sender.NavigationCompleted -= Finished; done.TrySetResult(); }
        core.NavigationCompleted += Finished;
        core.Navigate(url);
        await Task.WhenAny(done.Task, Task.Delay(10_000));
    }

    /// An expression in the page's own world (an extension's page, where its chrome.* APIs are), awaited.
    private static async Task<JsonObject?> Run(CoreWebView2 core, string expression)
    {
        try
        {
            return await RunAny(core, expression) as JsonObject;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or JsonException) { return null; }
    }

    private static async Task<JsonNode?> RunAny(CoreWebView2 core, string expression)
    {
        // An extension's page that never answers (it closed, or its worker is gone) doesn't hold the window up.
        var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true })).AsTask();
        try { return await Task.WhenAny(call, Task.Delay(10_000)) == call ? JsonNode.Parse(await call)?["result"]?["value"]?.DeepClone() : null; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or JsonException or ObjectDisposedException) { return null; }
    }

    // MARK: the side panel

    private double SideWidth { get => App.Notes.TryGetValue(SideWidthKey, out var w) && double.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 280, 900) : 380; set => App.Notes[SideWidthKey] = Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture); }

    private async Task OpenSide(string id, string? url = null)
    {
        CloseSide();
        if (_extensions.List.Find(id) is not { Enabled: true } item) return;
        url ??= _extensions.ManifestOf(id)?.SidePanel is { } path ? ExtensionHost.PageUrl(id, path) : null;
        if (url is null) return;
        SidePanel.Visibility = Visibility.Visible;
        LaySide();
        SideTitle.Text = item.Name;
        SideIcon.Source = _extensions.IconFile(id, 32) is { } icon ? new BitmapImage(new Uri(icon)) : null;
        SideIcon.Visibility = SideIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        var store = ExtensionStore;
        var view = await _pages.Surface(SideStage, store, ExtensionHost.PageShim("panel", await FrontWindow(), _surfaceWindows));
        _side = (id, view, store);
        view.Opacity = 1;
        view.IsHitTestVisible = true;
        view.CoreWebView2.WindowCloseRequested += (_, _) => { if (_side?.View == view) CloseSide(); };
        if (await Pages.WindowIdOf(view.CoreWebView2) is { } own) _surfaceWindows.Add(own);
        view.CoreWebView2.Navigate(url);
    }

    private void CloseSide()
    {
        if (_side is { } side) { _side = null; SideStage.Children.Remove(side.View); side.View.Close(); }
        SidePanel.Visibility = Visibility.Collapsed;
        LaySide();
    }

    private void OnSideClose(object sender, RoutedEventArgs e) => CloseSide();

    /// The panel stays open from tab to tab; its page is told which window is in front each time.
    private async Task FollowFront()
    {
        if (_side is not { } side) return;
        if (side.Store != ExtensionStore) { CloseSide(); return; }
        if (await FrontWindow() is { } window && _side?.View == side.View) await side.View.CoreWebView2.ExecuteScriptAsync($"window.__searchFront && window.__searchFront({window})");
    }

    private void LaySide() => SideDef.Width = new GridLength(SidePanel.Visibility == Visibility.Visible && !_fullScreen ? SideWidth + 8 : 0);

    private void WireSideHandle()
    {
        var dragging = false;
        // WinUI keeps an element's cursor protected; the handle is a plain Border, so it's set from outside.
        void Cursor(bool on) => typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .SetValue(SideHandle, on ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast) : null);
        SideHandle.PointerEntered += (_, _) => Cursor(true);
        SideHandle.PointerExited += (_, _) => { if (!dragging) Cursor(false); };
        SideHandle.PointerPressed += (_, e) => { dragging = SideHandle.CapturePointer(e.Pointer); e.Handled = true; };
        SideHandle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var x = e.GetCurrentPoint(Root).Position.X;
            SideWidth = Math.Clamp(Root.ActualWidth - x - 8, 280, Math.Max(280, Math.Min(900, Root.ActualWidth - 420)));
            LaySide();
        };
        SideHandle.PointerReleased += (_, e) => { dragging = false; SideHandle.ReleasePointerCapture(e.Pointer); Cursor(false); };
    }

    /// Where the buttons sit: the foot of the column, or beside the tabs across the top.
    private void LayExtensions()
    {
        Detach(ExtensionsBar);
        // Across the top, the agent's Stop line comes before the extensions' buttons, at the end of the tabs.
        _topEnd.Children.Clear();
        if (_prefs.TabsOnSide) { ColumnExtensionsSlot.Child = ExtensionsBar; TopExtensionsSlot.Child = null; }
        else { _topEnd.Children.Add(ExtensionsBar); TopExtensionsSlot.Child = _topEnd; ColumnExtensionsSlot.Child = null; }
        LayAgent(_topEnd);
        LaySide();
    }

    // MARK: the panel

    private sealed record StoreAdd(string Id);

    private List<PanelRow> ExtensionRows(string words)
    {
        var rows = new List<PanelRow>();
        var id = Crx.Id(words);
        if (id is not null && _extensions.List.Find(id) is null)
            rows.Add(new PanelRow { Item = new StoreAdd(id), Glyph = "", Title = _extensions.Busy.Contains(id) ? "Adding…" : "Add from the Chrome Web Store", Detail = id });
        foreach (var item in _extensions.List.Items.Where(i => words.Length == 0 || id is not null || i.Name.Contains(words, StringComparison.OrdinalIgnoreCase)))
        {
            var detail = new[] { item.Version, item.FromStore ? "Chrome Web Store" : item.Source, item.Enabled ? null : "off", _extensions.Errors.GetValueOrDefault(item.Id) };
            rows.Add(new PanelRow { Item = item, Glyph = "", Title = item.Name, Detail = string.Join(" · ", detail.Where(d => !string.IsNullOrEmpty(d))), ActionGlyph = "", ActionName = "More" });
        }
        if (words.Length == 0)
        {
            rows.Add(new PanelRow { Item = "store", Glyph = "", Title = "Chrome Web Store", Detail = "Find extensions; “Add to Search” on an extension's page adds it" });
            if (_extensions.List.Items.Any(i => i.FromStore)) rows.Add(new PanelRow { Item = "updates", Glyph = "", Title = "Check for updates", Detail = "Search checks once a day by itself" });
        }
        return rows;
    }

    private void TakeExtensionRow(PanelRow row)
    {
        switch (row.Item)
        {
            case StoreAdd add: _ = AddFromStore(add.Id); PanelSearch.Text = ""; break;
            case "store": Go(new Uri(WebStore), newTab: true); ClosePanel(); break;
            case "updates": _ = CheckNow(); break;
            case Installed item when PanelRows.ContainerFromItem(row) is FrameworkElement at: ExtensionMenu(item).ShowAt(at); break;
        }
    }

    private async Task CheckNow()
    {
        var before = _extensions.List.Items.ToDictionary(i => i.Id, i => i.Version);
        await _extensions.CheckForUpdates(m => ConfirmInstall(m, updating: true), force: true);
        var updated = _extensions.List.Items.Count(i => before.TryGetValue(i.Id, out var v) && v != i.Version);
        Notify(updated == 0 ? "Every extension is up to date." : $"Updated {updated} extension{(updated == 1 ? "" : "s")}.");
    }

    // MARK: notices

    /// A word about what just happened, on the window's own notice card.
    public void Notify(string text) { LastNotice = text; Notice(text); }

    /// For the bench.
    private string? LastNotice { get; set; }
}
