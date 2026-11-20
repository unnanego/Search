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

    /// A page finished loading in an ordinary tab: for the history.
    public event Action<Tab, Uri, string>? Visited;
    public event Action<Tab, string>? TitleChanged;
    /// A page asked for full screen, or let it go.
    public event Action<Tab, bool>? FullScreen;
    /// One of the window's keys pressed while a page had the keyboard, by name ("ctrl+t").
    public event Action<Tab, string>? KeyPressed;

    /// The window's keys, by name. A web view keeps the keyboard in its own process and WinUI hands none of it to the
    /// window, so each page gets a listener at document start that passes these keys, and only these, to the window.
    public IReadOnlyCollection<string> Keys { get; set; } = [];
    /// Marks the listener's messages: a page can post to the window too, but can't read the listener's closure.
    private readonly string _keyToken = Guid.NewGuid().ToString("N");

    public Pages(Browser browser, Grid stage)
    {
        _browser = browser;
        _stage = stage;
        browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) _ = Show(); };
        browser.Tabs.CollectionChanged += (_, _) => { foreach (var gone in _views.Keys.Where(t => !browser.Tabs.Contains(t)).ToList()) Sleep(gone); };
    }

    public WebView2? ViewOf(Tab tab) => _views.GetValueOrDefault(tab);
    public CoreWebView2? CoreOf(Tab tab) => _views.GetValueOrDefault(tab)?.CoreWebView2;
    public IEnumerable<Tab> Live => _views.Keys;

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; foreach (var view in _views.Values) Paint(view.CoreWebView2); }
    }

    /// The tab in front gets a web view now if it has none and somewhere to go, and only it is shown. An empty tab
    /// waiting for an address gets none until it has one.
    public async Task Show()
    {
        Arrange();
        if (_browser.Selected is { Url: not null } front) await Ensure(front);
        Arrange();
    }

    private void Arrange()
    {
        foreach (var (tab, view) in _views)
        {
            var front = tab == _browser.Selected;
            view.Visibility = front || tab.Bench ? Visibility.Visible : Visibility.Collapsed;
            view.Opacity = tab.Bench ? 0 : 1;
            view.IsHitTestVisible = front;
            if (front && _stage.Children.IndexOf(view) != _stage.Children.Count - 1) { _stage.Children.Remove(view); _stage.Children.Add(view); }
        }
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
        _stage.Children.Insert(0, view);
        var environment = await Web.Environment;
        if (tab.Private)
        {
            // A profile of its own, InPrivate, per tab: its own cookie jar, gone when the tab closes.
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            options.ProfileName = "private-" + tab.ShortId;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        else await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        Privacy.Apply(core.Settings);
        Paint(core);
        Wire(tab, core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyListener());
        _views[tab] = view;
        _building.Remove(tab);
        tab.Live = true;
        tab.NavigateRequested += Navigate;
        tab.CommandRequested += Command;
        if (navigate && tab.Url is { } url) core.Navigate(url.AbsoluteUri);
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
        core.NavigationStarting += (_, _) => tab.Loading = true;
        core.SourceChanged += (_, _) => { if (Uri.TryCreate(core.Source, UriKind.Absolute, out var url) && url.AbsoluteUri != "about:blank") tab.Url = url; };
        core.DocumentTitleChanged += (_, _) => { tab.Title = core.DocumentTitle; TitleChanged?.Invoke(tab, core.DocumentTitle); };
        core.HistoryChanged += (_, _) => { tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward; };
        core.FaviconChanged += (_, _) => tab.Icon = string.IsNullOrEmpty(core.FaviconUri) ? null : core.FaviconUri;
        core.ContainsFullScreenElementChanged += (_, _) => FullScreen?.Invoke(tab, core.ContainsFullScreenElement);
        core.WebMessageReceived += (_, e) => OnKeyMessage(tab, e.WebMessageAsJson);
        // Keys pressed inside a frame (an embedded editor, a video) come through the frame's own channel.
        core.FrameCreated += (_, e) => e.Frame.WebMessageReceived += (_, m) => OnKeyMessage(tab, m.WebMessageAsJson);
        core.NavigationCompleted += (_, e) =>
        {
            tab.Loading = false;
            tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward;
            // Bench and private tabs never enter the history.
            if (e.IsSuccess && !tab.Bench && !tab.Private && Uri.TryCreate(core.Source, UriKind.Absolute, out var url)) Visited?.Invoke(tab, url, core.DocumentTitle);
        };
        // Always handled: a new window WebView2 opens on its own outlives the app and keeps its browser process alive.
        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var opened = _browser.Open(null, select: !tab.Bench, after: tab, bench: tab.Bench, @private: tab.Private);
                var view = await Ensure(opened, navigate: false);
                e.NewWindow = view.CoreWebView2;
                e.Handled = true;
            }
            finally { deferral.Complete(); }
        };
    }

    /// Capture phase, at the window, before anything the page adds; real key presses only (isTrusted), so a page can't
    /// press the window's keys by dispatching events; layout-independent names from KeyboardEvent.code.
    private string KeyListener() => $$"""
        (() => {
          const token = {{JsonSerializer.Serialize(_keyToken)}}, keys = new Set({{JsonSerializer.Serialize(Keys)}});
          const view = window.chrome && window.chrome.webview;
          if (!view) return;
          const post = view.postMessage.bind(view);
          const named = { BracketLeft: '[', BracketRight: ']', Comma: ',', ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down',
                          PageUp: 'pageup', PageDown: 'pagedown', Tab: 'tab', Escape: 'esc', Enter: 'enter', Equal: '=', Minus: '-' };
          // By the key's place (code); where there is none (an on-screen keyboard, some remote sessions), by keyCode,
          // which is the virtual key and so the same whatever the layout.
          const byCode = { 9: 'tab', 13: 'enter', 27: 'esc', 33: 'pageup', 34: 'pagedown', 37: 'left', 38: 'up', 39: 'right', 40: 'down', 187: '=', 188: ',', 189: '-', 219: '[', 221: ']' };
          const fromKeyCode = k => k >= 65 && k <= 90 ? String.fromCharCode(k + 32) : k >= 48 && k <= 57 ? String(k - 48) : k >= 112 && k <= 135 ? 'f' + (k - 111) : byCode[k] || '';
          const name = e => { const c = e.code || ''; return c.startsWith('Key') ? c.slice(3).toLowerCase() : c.startsWith('Digit') ? c.slice(5) : named[c] || (c ? c.toLowerCase() : fromKeyCode(e.keyCode)); };
          addEventListener('keydown', e => {
            if (!e.isTrusted) return;
            const combo = [e.ctrlKey && 'ctrl', e.shiftKey && 'shift', e.altKey && 'alt', name(e)].filter(Boolean).join('+');
            if (!keys.has(combo)) return;
            e.preventDefault(); e.stopImmediatePropagation();
            post({ search: token, combo });
          }, true);
        })();
        """;

    private void OnKeyMessage(Tab tab, string json)
    {
        try
        {
            using var message = JsonDocument.Parse(json);
            if (message.RootElement.ValueKind == JsonValueKind.Object && message.RootElement.TryGetProperty("search", out var token) && token.GetString() == _keyToken
                && message.RootElement.TryGetProperty("combo", out var combo) && combo.GetString() is { } name && Keys.Contains(name))
                KeyPressed?.Invoke(tab, name);
        }
        catch (JsonException) { }
    }

    private void Navigate(Tab tab, Uri url) { if (CoreOf(tab) is { } core) core.Navigate(url.AbsoluteUri); }

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

    /// The tab's web view let go of: closed, or asleep. Its address and title stay with the tab.
    public void Sleep(Tab tab)
    {
        if (!_views.Remove(tab, out var view)) return;
        tab.NavigateRequested -= Navigate;
        tab.CommandRequested -= Command;
        tab.Live = false;
        tab.Loading = false;
        _stage.Children.Remove(view);
        view.Close();
    }

    /// A script's value back as a .NET string, or null.
    public static string? Unquote(string json) => json == "null" ? null : JsonSerializer.Deserialize<string>(json);
}
