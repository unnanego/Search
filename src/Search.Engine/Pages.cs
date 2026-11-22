using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

// The web views behind a window's tabs (Tab.swift's engine half). A tab gets its web view the first time it's shown
// and keeps it until it closes or sleeps. Every live view sits in the one stage: the tab in front visible on top, the
// others collapsed (so Chromium treats them as hidden), and bench tabs drawn but transparent underneath, so a script
// can load and photograph them without ever putting them in front of you.
public sealed class Pages
{
    private readonly Browser _browser;
    private readonly Grid _stage;
    private readonly Dictionary<Tab, WebView2> _views = new();
    private readonly Dictionary<Tab, Task<WebView2>> _building = new();
    private Theme _theme;

    /// A page took the keyboard (a click into it): in a split, that page is the one in front now.
    public event Action<Tab>? Focused;
    /// A page finished loading in any tab, bench and private ones included: for what reads the page (sign-ins).
    public event Action<Tab>? Loaded;
    /// A tab's web view let go of, because the tab closed or slept.
    public event Action<Tab>? Released;
    /// A page finished loading in an ordinary tab: for the history.
    public event Action<Tab, Uri, string>? Visited;
    public event Action<Tab, string>? TitleChanged;
    /// A page asked for full screen, or let it go.
    public event Action<Tab, bool>? FullScreen;
    /// One of the window's keys pressed while a page had the keyboard, by name ("ctrl+t").
    public event Action<Tab, string>? KeyPressed;
    /// A page asked for something only you can give it (the camera, where you are, notifications). The window answers;
    /// the request waits on its deferral until it does.
    public event Action<Tab, CoreWebView2PermissionRequestedEventArgs, Windows.Foundation.Deferral>? PermissionAsked;
    /// A page sent a notification. WebView2 shows none of its own; the window posts it, or keeps it in a test run.
    public event Action<Tab, CoreWebView2Notification>? Notified;
    /// A download began. WebView2's own downloads flyout is kept away; the window shows its own.
    public event Action<Tab, CoreWebView2DownloadOperation>? DownloadStarted;
    /// Anything else Search's own scripts in a page say (see World): its kind, and the message.
    public event Action<Tab, string, JsonElement>? Message;

    /// The window's keys, by name. A web view keeps the keyboard in its own process and WinUI hands none of it to the
    /// window, so each page gets a listener at document start that passes these keys, and only these, to the window.
    public IReadOnlyCollection<string> Keys { get; set; } = [];

    private readonly Dictionary<Tab, World> _worlds = new();
    /// Tabs of the spaces not on screen: out of the row, their pages kept as they are, hidden, until their space is back.
    public HashSet<Tab> Parked { get; } = new();
    /// Where a peek's page is drawn: the peek panel, over the page, not the stage.
    public Panel? PeekHost { get; set; }
    private readonly Dictionary<Tab, (string? Page, string? Skipper)> _youtube = new();
    private bool _shieldOn = true;
    private IReadOnlyCollection<string> _shieldPaused = [];
    /// Each profile's extensions brought in step before its first page loads (ExtensionHost.Ready).
    public Func<CoreWebView2Profile, Task>? Extensions { get; set; }
    /// Each live tab's window, as extensions number it: the one the DevTools protocol reports for its web view.
    private readonly Dictionary<Tab, long> _windows = new();

    /// Shield on or off, and the sites it's paused on: every profile and every page told.
    public async Task SetShield(bool on, IReadOnlyCollection<string> paused)
    {
        (_shieldOn, _shieldPaused) = (on, paused.ToList());
        await Shield.Update(_views.Values.Select(v => v.CoreWebView2.Profile), on, _shieldPaused);
        foreach (var (tab, view) in _views) await YouTube(tab, view.CoreWebView2);
    }

    /// YouTube's two scripts (Shield.cs), put in again for the pauses as they are now.
    private async Task YouTube(Tab tab, CoreWebView2 core)
    {
        if (_youtube.Remove(tab, out var old))
        {
            if (old.Page is { } page) core.RemoveScriptToExecuteOnDocumentCreated(page);
            if (old.Skipper is { } skipper && _worlds.TryGetValue(tab, out var w)) await w.Remove(skipper);
        }
        if (!_shieldOn || !_worlds.TryGetValue(tab, out var world)) return;
        _youtube[tab] = (await core.AddScriptToExecuteOnDocumentCreatedAsync(Shield.YouTubePage(_shieldPaused)), await world.Add(Shield.YouTubeSkipper(_shieldPaused)));
    }
    private readonly List<string> _scripts = new();

    private readonly List<string> _pageScripts = new();

    /// A script for every page to come in the page's own world, before its scripts; for what must stand in for an API
    /// the page itself calls (the store's install). It sees what the page sees, so it decides nothing on its own.
    public void AddPageScript(string source) => _pageScripts.Add(source);

    /// A script of Search's own for every page, in the isolated world: those open now and those to come.
    public void AddScript(string source)
    {
        _scripts.Add(source);
        foreach (var world in _worlds.Values) _ = world.Add(source);
    }

    /// Runs an expression in a tab's isolated world; null if the tab has no web view or the page no world yet.
    public Task<System.Text.Json.Nodes.JsonNode?> Evaluate(Tab tab, string expression) =>
        _worlds.TryGetValue(tab, out var world) ? world.Evaluate(expression) : Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null);

    /// A tab's isolated world, and through it its CoreWebView2; null if it has no web view.
    public World? WorldOf(Tab tab) => _worlds.GetValueOrDefault(tab);

    public Pages(Browser browser, Grid stage)
    {
        _browser = browser;
        _stage = stage;
        browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) _ = Show(); };
        browser.SplitsChanged += () => _ = Show();
        stage.SizeChanged += (_, _) => Arrange();
        browser.Tabs.CollectionChanged += (_, _) => { foreach (var gone in _views.Keys.Where(t => !t.Peek && !Parked.Contains(t) && !browser.Tabs.Contains(t)).ToList()) Sleep(gone); };
    }

    public WebView2? ViewOf(Tab tab) => _views.GetValueOrDefault(tab);
    public CoreWebView2? CoreOf(Tab tab) => _views.GetValueOrDefault(tab)?.CoreWebView2;
    public IEnumerable<Tab> Live => _views.Keys;

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; foreach (var view in _views.Values) Paint(view.CoreWebView2); }
    }

    /// Where each page on screen is, in the stage, after each layout: for the split's dividers and outline.
    public IReadOnlyList<(Tab Tab, Windows.Foundation.Rect Rect)> Panes { get; private set; } = [];
    public event Action? Arranged;
    /// The room between two pages of a split, where its divider is.
    public const double Gap = 6;

    /// The pages on screen: the tab in front, or every page of its split.
    public IReadOnlyList<Tab> OnScreen => _browser.SplitOf(_browser.Selected) is { } split ? split.Tabs : _browser.Selected is { } front ? [front] : [];

    /// The pages on screen get a web view now if they have none and somewhere to go, and only they are shown. An empty
    /// tab waiting for an address gets none until it has one.
    public async Task Show()
    {
        Arrange();
        foreach (var tab in OnScreen.Where(t => t.Url is not null).ToList()) await Ensure(tab);
        Arrange();
    }

    private void Arrange()
    {
        var split = _browser.SplitOf(_browser.Selected);
        var shown = OnScreen;
        var panes = new List<(Tab, Windows.Foundation.Rect)>();
        double width = _stage.ActualWidth, height = _stage.ActualHeight, offset = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            var tab = shown[i];
            Windows.Foundation.Rect rect;
            if (split is null) rect = new(0, 0, width, height);
            else
            {
                var gaps = Gap * (shown.Count - 1);
                var horizontal = split.Axis == SplitAxis.Horizontal;
                var length = Math.Max(0, ((horizontal ? width : height) - gaps) * split.Sizes[i]);
                rect = horizontal ? new(offset, 0, length, height) : new(0, offset, width, length);
                offset += length + Gap;
            }
            panes.Add((tab, rect));
        }
        foreach (var (tab, view) in _views)
        {
            if (tab.Peek) continue;
            var at = panes.FindIndex(p => p.Item1 == tab);
            var visible = at >= 0;
            view.Visibility = visible || tab.Bench ? Visibility.Visible : Visibility.Collapsed;
            view.Opacity = tab.Bench ? 0 : 1;
            view.IsHitTestVisible = visible;
            if (visible && split is not null)
            {
                var rect = panes[at].Item2;
                (view.HorizontalAlignment, view.VerticalAlignment) = (HorizontalAlignment.Left, VerticalAlignment.Top);
                (view.Width, view.Height, view.Margin) = (rect.Width, rect.Height, new Thickness(rect.X, rect.Y, 0, 0));
            }
            else
            {
                (view.HorizontalAlignment, view.VerticalAlignment) = (HorizontalAlignment.Stretch, VerticalAlignment.Stretch);
                (view.Width, view.Height, view.Margin) = (double.NaN, double.NaN, new Thickness(0));
            }
            if (visible && _stage.Children.IndexOf(view) != _stage.Children.Count - 1 - (shown.Count - 1 - at)) { _stage.Children.Remove(view); _stage.Children.Add(view); }
        }
        Panes = panes;
        Arranged?.Invoke();
    }

    public Task<WebView2> Ensure(Tab tab, bool navigate = true)
    {
        if (_views.TryGetValue(tab, out var live)) return Task.FromResult(live);
        if (_building.TryGetValue(tab, out var building)) return building;
        var task = Build(tab, navigate);
        _building[tab] = task;
        return task;
    }

    private async Task<WebView2> Build(Tab tab, bool navigate)
    {
        // Built drawn but transparent and at the back: a web view that isn't in the tree, or is collapsed, never
        // finishes starting. Arrange puts it where it belongs once it has.
        var view = new WebView2 { Opacity = 0, IsHitTestVisible = false, IsTabStop = false };
        // A web view takes the keyboard when it starts; whoever had it (the field, as you type) gets it back.
        var focused = _stage.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        if (tab.Peek && PeekHost is { } peekHost) { view.Opacity = 1; view.IsHitTestVisible = true; peekHost.Children.Add(view); }
        else _stage.Children.Insert(0, view);
        // A click into the page, not the window giving it the keys: only that says this page is the one in front now.
        view.GotFocus += (_, _) => { if (view.FocusState == FocusState.Pointer) Focused?.Invoke(tab); };
        if (tab.Private)
        {
            // In private tabs' own browser (Web.Private), InPrivate, a profile per tab: its own cookie jar, gone when
            // the tab closes.
            var environment = await Web.Private;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            options.ProfileName = "private-" + tab.ShortId;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        else if (tab.Store is { } store)
        {
            // Its space's own sign-ins: a profile of its own in the shared browser.
            var environment = await Web.Environment;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = store;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        else await view.EnsureCoreWebView2Async(await Web.Environment);
        var core = view.CoreWebView2;
        Privacy.Apply(core.Settings);
        // A test run downloads into its own world, never into the Downloads folder of whoever runs it.
        if (Store.Testing) core.Profile.DefaultDownloadFolderPath = Store.File("Downloads");
        Paint(core);
        Wire(tab, core);
        // A peek also passes Esc: it's how a peek is put away, and the page under it can't hear it.
        var world = await World.Open(core, _scripts.Prepend(KeyListener(tab.Peek ? Keys.Append("esc").ToList() : Keys)));
        world.Message += (kind, body) => OnMessage(tab, kind, body);
        _worlds[tab] = world;
        // A coding agent's page never has a file chosen for it: the chooser is caught and never shown.
        if (tab.Agent) { try { await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", """{"enabled":true}"""); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { } }
        // The ad blocker before the first request, as the Mac holds the first loads until its list is ready.
        await Shield.Protect(core.Profile, _shieldOn, _shieldPaused);
        await YouTube(tab, core);
        foreach (var script in _pageScripts) await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        // Extensions don't run in private tabs, which have a browser of their own.
        if (!tab.Private && Extensions is { } extensions)
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionHost.PageShim("tab", null, []));
            await extensions(core.Profile);
        }
        _views[tab] = view;
        _building.Remove(tab);
        tab.Live = true;
        tab.NavigateRequested += Navigate;
        tab.CommandRequested += Command;
        // Loading from the moment it's asked to, not from the first event: a tab in that moment isn't idle.
        if (navigate && tab.Url is { } url) { tab.Loading = true; core.Navigate(url.AbsoluteUri); }
        Arrange();
        view.IsTabStop = true;
        // Only if the new web view took it: by now the keys may rightly be somewhere else.
        if (focused is not null && ReferenceEquals(FocusManager.GetFocusedElement(_stage.XamlRoot), view)) focused.Focus(FocusState.Programmatic);
        return view;
    }

    private void Paint(CoreWebView2 core) => core.Profile.PreferredColorScheme = _theme switch
    {
        Theme.Light => CoreWebView2PreferredColorScheme.Light,
        Theme.Dark => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto
    };

    private void Wire(Tab tab, CoreWebView2 core)
    {
        if (tab.Agent) Confine(core);
        core.NavigationStarting += (_, e) => { if (!e.Cancel) tab.Loading = true; };
        core.SourceChanged += (_, _) => { if (Uri.TryCreate(core.Source, UriKind.Absolute, out var url) && url.AbsoluteUri != "about:blank") tab.Url = url; };
        core.DocumentTitleChanged += (_, _) => { tab.Title = core.DocumentTitle; TitleChanged?.Invoke(tab, core.DocumentTitle); };
        core.HistoryChanged += (_, _) => { tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward; };
        core.FaviconChanged += (_, _) => tab.Icon = string.IsNullOrEmpty(core.FaviconUri) ? null : core.FaviconUri;
        core.ContainsFullScreenElementChanged += (_, _) => FullScreen?.Invoke(tab, core.ContainsFullScreenElement);
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.Audible = core.IsDocumentPlayingAudio;
        core.DownloadStarting += (_, e) => { e.Handled = true; if (tab.Agent) e.Cancel = true; else DownloadStarted?.Invoke(tab, e.DownloadOperation); };
        core.PermissionRequested += (_, e) =>
        {
            // A coding agent's tab never sees, hears, locates or notifies you, whatever a site was allowed before.
            if (tab.Agent) { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; return; }
            // Asked of WebView2's own question only when nobody in the window is listening.
            if (PermissionAsked is null) return;
            e.Handled = true;
            PermissionAsked(tab, e, e.GetDeferral());
        };
        core.NotificationReceived += (_, e) => { e.Handled = true; if (!tab.Agent) Notified?.Invoke(tab, e.Notification); };
        core.NavigationCompleted += (_, e) =>
        {
            tab.Loading = false;
            tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward;
            if (e.IsSuccess) Loaded?.Invoke(tab);
            // Bench and private tabs never enter the history.
            if (e.IsSuccess && !tab.Bench && !tab.Private && Uri.TryCreate(core.Source, UriKind.Absolute, out var url)) Visited?.Invoke(tab, url, core.DocumentTitle);
        };
        // Always handled: a new window WebView2 opens on its own outlives the app and keeps its browser process alive.
        core.NewWindowRequested += (_, e) => Adopt(e, tab);
    }

    /// A coding agent's tab stays on the web: none of Search's own pages, no file:// and no link handed to another app,
    /// whoever asked (the agent, the page, or a link it clicked); no download, no sign-in box, no dialog over your page.
    private static void Confine(CoreWebView2 core)
    {
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var url) || !AgentRules.MayLoad(url)) e.Cancel = true; };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
    }

    /// A page opened another tab: the new one, and the one it came from. A coding agent's tab's are its own too.
    public event Action<Tab, Tab>? Opened;

    /// A new window a page or an extension asked for, as a tab of the row's beside the one it came from.
    private async void Adopt(CoreWebView2NewWindowRequestedEventArgs e, Tab? from)
    {
        var deferral = e.GetDeferral();
        try
        {
            var opened = _browser.Open(null, select: from is not { Bench: true }, after: from ?? _browser.Selected, bench: from?.Bench ?? false, @private: from?.Private ?? false, agent: from?.Agent ?? false);
            var view = await Ensure(opened, navigate: false);
            e.NewWindow = view.CoreWebView2;
            e.Handled = true;
            if (from is not null) Opened?.Invoke(opened, from);
            SurfaceOpened?.Invoke(opened);
        }
        catch (Exception x) { System.Diagnostics.Debug.WriteLine(x); }
        finally { deferral.Complete(); }
    }

    /// A tab opened from one of Search's surfaces, or by an extension: the popup it came from goes, as Chrome's does.
    public event Action<Tab>? SurfaceOpened;

    /// The window a tab is to extensions, asked once of its live web view.
    public async Task<long?> WindowIdOf(Tab tab)
    {
        if (_windows.TryGetValue(tab, out var known)) return known;
        if (CoreOf(tab) is not { } core || await WindowIdOf(core) is not { } id) return null;
        return _windows[tab] = id;
    }

    public static async Task<long?> WindowIdOf(CoreWebView2 core)
    {
        try { return JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Browser.getWindowForTarget", "{}")).RootElement.GetProperty("windowId").GetInt64(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or KeyNotFoundException or JsonException or InvalidOperationException or ObjectDisposedException) { return null; }
    }

    /// A web view of Search's own for one of an extension's pages (its popup, its side panel), in the profile of the
    /// tabs it serves, with the page shim at document start. Not a tab: never in the row, the session or the history.
    public async Task<WebView2> Surface(Panel host, string? store, string shim)
    {
        var view = new WebView2 { Opacity = 0, IsHitTestVisible = false };
        host.Children.Add(view);
        var environment = await Web.Environment;
        if (store is not null)
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = store;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        else await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        Privacy.Apply(core.Settings);
        Paint(core);
        core.DownloadStarting += (_, e) => { e.Handled = true; DownloadStarted?.Invoke(_browser.Selected ?? new Tab(), e.DownloadOperation); };
        core.NewWindowRequested += (_, e) => Adopt(e, null);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(shim);
        if (Extensions is { } extensions) await extensions(core.Profile);
        return view;
    }

    /// Capture phase, at the window, before anything the page adds; real key presses only (isTrusted), so a page can't
    /// press the window's keys by dispatching events; names from KeyboardEvent.code, or keyCode where there's no code
    /// (an on-screen keyboard, some remote sessions), so the layout never matters. It runs in the isolated world: the
    /// page can't see it, and only it can reach the window.
    private static string KeyListener(IReadOnlyCollection<string> keys) => $$"""
        (() => {
          const keys = new Set({{JsonSerializer.Serialize(keys)}});
          const named = { BracketLeft: '[', BracketRight: ']', Backslash: '\\\\', Comma: ',', ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down',
                          PageUp: 'pageup', PageDown: 'pagedown', Tab: 'tab', Escape: 'esc', Enter: 'enter', Equal: '=', Minus: '-' };
          const byCode = { 220: '\\\\', 9: 'tab', 13: 'enter', 27: 'esc', 33: 'pageup', 34: 'pagedown', 37: 'left', 38: 'up', 39: 'right', 40: 'down', 187: '=', 188: ',', 189: '-', 219: '[', 221: ']' };
          const fromKeyCode = k => k >= 65 && k <= 90 ? String.fromCharCode(k + 32) : k >= 48 && k <= 57 ? String(k - 48) : k >= 112 && k <= 135 ? 'f' + (k - 111) : byCode[k] || '';
          const name = e => { const c = e.code || ''; return c.startsWith('Key') ? c.slice(3).toLowerCase() : c.startsWith('Digit') ? c.slice(5) : named[c] || (c ? c.toLowerCase() : fromKeyCode(e.keyCode)); };
          addEventListener('keydown', e => {
            if (!e.isTrusted) return;
            const combo = [e.ctrlKey && 'ctrl', e.shiftKey && 'shift', e.altKey && 'alt', name(e)].filter(Boolean).join('+');
            if (!keys.has(combo)) return;
            e.preventDefault(); e.stopImmediatePropagation();
            __searchSend({ kind: 'key', combo });
          }, true);
        })();
        """;

    /// World messages heard per tab, for tests.
    public Dictionary<Tab, int> Heard { get; } = new();

    private void OnMessage(Tab tab, string kind, JsonElement body)
    {
        Heard[tab] = Heard.GetValueOrDefault(tab) + 1;
        if (kind != "key") { Message?.Invoke(tab, kind, body); return; }
        // A coding agent's keys go to its page, never through the window: none of Search's shortcuts hears them.
        if (tab.Agent) return;
        if (body.TryGetProperty("combo", out var combo) && combo.GetString() is { } name && (Keys.Contains(name) || tab.Peek && name == "esc")) KeyPressed?.Invoke(tab, name);
    }
    private void Navigate(Tab tab, Uri url) { if (CoreOf(tab) is { } core) { tab.Loading = true; core.Navigate(url.AbsoluteUri); } }

    private void Command(Tab tab, TabCommand command)
    {
        if (CoreOf(tab) is not { } core) return;
        switch (command)
        {
            case TabCommand.Back when core.CanGoBack: core.GoBack(); break;
            case TabCommand.Forward when core.CanGoForward: core.GoForward(); break;
            case TabCommand.Reload: core.Reload(); break;
            // Everything cached checked with the site again, as Reload Page From Origin.
            case TabCommand.ReloadFromOrigin: _ = core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}"""); break;
            case TabCommand.Stop: core.Stop(); break;
        }
    }

    /// A peek kept: its page, loaded as it is, moved from the peek panel to the stage, its tab now one of the row's.
    public void Keep(Tab peek)
    {
        if (!_views.TryGetValue(peek, out var view)) return;
        PeekHost?.Children.Remove(view);
        _stage.Children.Insert(0, view);
        peek.Peek = false;
        Arrange();
    }

    /// The tab's web view let go of: closed, or asleep. Its address and title stay with the tab.
    public void Sleep(Tab tab)
    {
        if (!_views.Remove(tab, out var view)) return;
        _worlds.Remove(tab);
        _youtube.Remove(tab);
        _windows.Remove(tab);
        tab.NavigateRequested -= Navigate;
        tab.CommandRequested -= Command;
        tab.Live = false;
        tab.Loading = false;
        _stage.Children.Remove(view);
        PeekHost?.Children.Remove(view);
        view.Close();
        Released?.Invoke(tab);
    }

    /// A script's value back as a .NET string, or null.
    public static string? Unquote(string json) => json == "null" ? null : JsonSerializer.Deserialize<string>(json);
}
