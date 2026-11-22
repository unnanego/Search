using System.Runtime.InteropServices;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Search.Core;
using Windows.Graphics;
using Windows.System;

namespace Search.App;

// The address field from any app (Launcher.swift, HotKey.swift; Settings › From any app). A key of your own raises it
// over whichever app is in front. What is typed there is answered as Ctrl+L answers it, from the window in front's
// profile: the pages already open in its windows first, then your bookmarks and where you have been. Enter goes to the
// tab that has it, or opens it as a link from another app would, and brings Search forward. Escape, Ctrl+W or the key
// again puts it away and gives the keys back to the app you were in; a click anywhere else puts it away too.
//
// Only while Search isn't in front with a window: there the key is given back to the page (UnregisterHotKey), so a
// key chosen for the launcher still types in Search's own pages. Windows tells us whenever the window in front changes
// (SetWinEventHook), and the key is asked for or given back then.
//
// Its own field and list, not a window's: a window's field shows what its own tabs are doing. Nothing is asked of a
// search engine while typing: words typed over another app are not a page Search was asked to show.
public static class Launcher
{
    private const int HotKeyId = 0x5EA;
    private static Prefs? _prefs;
    private static DispatcherQueue? _queue;
    private static LauncherWindow? _window;
    private static MainWindow? _target;
    private static nint _before;
    private static string _draft = "";
    private static LauncherKey? _registered;
    private static nint _hook, _hookMinimize;
    private static WinEventProc? _onForeground;
    private static SubclassProc? _onMessage;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _settle;

    public static bool Up { get; private set; }
    public static string Typed { get; private set; } = "";
    public static IReadOnlyList<Suggestion> Offers { get; private set; } = Array.Empty<Suggestion>();
    public static string? Ending { get; private set; }
    public static int? Picked { get; private set; }
    /// Why Windows wouldn't give Search the key chosen, for Settings to say.
    public static string? Refusal { get; private set; }
    public static bool Registered => _registered is not null;
    /// Settings is listening for a new key: the old one is given up meanwhile, so pressing it records it.
    public static bool Recording { get => _recording; set { _recording = value; Refresh(); } }
    private static bool _recording;
    /// What Settings shows changed: the key's refusal.
    public static event Action? Changed;

    public static void Start(Prefs prefs, DispatcherQueue queue)
    {
        if (_prefs is not null) return;
        _prefs = prefs;
        _queue = queue;
        _window = new LauncherWindow();
        _onMessage = OnMessage;
        SetWindowSubclass(_window.Handle, _onMessage, 1, 0);
        _onForeground = (_, _, _, _, _, _, _) => Soon();
        _settle = queue.CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(500);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Refresh();
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _onForeground, 0, 0, 0);
        _hookMinimize = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, 0, _onForeground, 0, Environment.ProcessId, 0);
        prefs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Prefs.LauncherKey)) Check();
            else if (e.PropertyName is nameof(Prefs.Launcher)) { if (prefs.Launcher && prefs.LauncherKey is null) prefs.LauncherKey = LauncherKey.Suggested.Display; Refresh(); }
        };
        Refresh();
    }

    /// The window in front changed, or one was minimised: asked again now, and again once a minimise has finished
    /// (Windows tells of it as it starts, while the window still shows).
    private static void Soon()
    {
        _queue?.TryEnqueue(Refresh);
        _queue?.TryEnqueue(() => { _settle?.Stop(); _settle?.Start(); });
    }

    public static void Stop()
    {
        Unregister();
        if (_hook != 0) UnhookWinEvent(_hook);
        if (_hookMinimize != 0) UnhookWinEvent(_hookMinimize);
        _hook = _hookMinimize = 0;
    }

    private static LauncherKey? Key => LauncherKey.Parse(_prefs?.LauncherKey);

    /// A window of Search's is in front: the foreground window is this process's, with one of its windows on screen.
    private static bool SearchInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == Environment.ProcessId;
    }

    private static bool WindowShowing => App.Windows.Any(w => w.Showing);

    public static bool Wants => _prefs is { } prefs && LauncherRules.Wants(prefs.Launcher, Key, _recording, Up, SearchInFront(), WindowShowing);

    /// The key asked of Windows, or given back, as things are now.
    public static void Refresh()
    {
        if (_prefs is not { } prefs) return;
        if (!prefs.Launcher || Key is null) { if (Up) Dismiss(keeping: false); Say(null); }
        if (!Wants || Key is not { } key) { Unregister(); return; }
        if (key.Refused() is { } why) { Unregister(); Say(why); return; }
        Register(key);
    }

    /// A key just chosen, asked of Windows at once, so Settings can say straight away if it is another app's, though
    /// Settings is in front, where the key would otherwise wait until Search goes behind.
    private static void Check()
    {
        if (_prefs is not { Launcher: true } || Key is not { } key) { Refresh(); return; }
        if (key.Refused() is { } why) { Unregister(); Say(why); return; }
        Register(key);
        if (!Wants) Unregister();
    }

    private static void Register(LauncherKey key)
    {
        if (_registered == key) return;
        Unregister();
        if (_window is null) return;
        if (RegisterHotKey(_window.Handle, HotKeyId, key.Modifiers | ModNoRepeat, key.Code)) { _registered = key; Say(null); }
        else Say($"Another app is using {key.Display}");
    }

    private static void Unregister()
    {
        if (_registered is null || _window is null) return;
        UnregisterHotKey(_window.Handle, HotKeyId);
        _registered = null;
    }

    private static void Say(string? refusal)
    {
        if (Refusal == refusal) return;
        Refusal = refusal;
        Changed?.Invoke();
    }

    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nint id, nint data)
    {
        if (message == WmHotKey && wParam == HotKeyId) { Toggle(); return 0; }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    // Up and away.

    public static void Toggle() { if (Up) Dismiss(); else Show(); }

    public static void Show()
    {
        if (_prefs is not { Launcher: true } || Up || _window is null) return;
        _target = App.Acting ?? App.Windows.FirstOrDefault();
        var front = GetForegroundWindow();
        GetWindowThreadProcessId(front, out var process);
        _before = process == Environment.ProcessId ? 0 : front;
        Up = true;
        Typed = _draft;
        _draft = "";
        Guess();
        _window.Present(Typed, Offers);
        Refresh();
    }

    /// Put away. What was typed waits for the next time, unless it went somewhere; the keys go back to the app it was
    /// raised over, unless a click elsewhere already took them.
    public static void Dismiss(bool keeping = true, bool giveBack = true) => Dismiss(keeping, giveBack, hide: true);

    private static void Dismiss(bool keeping, bool giveBack, bool hide)
    {
        if (!Up) return;
        _draft = keeping ? Typed : "";
        Up = false;
        Typed = "";
        Offers = Array.Empty<Suggestion>();
        Ending = null;
        Picked = null;
        if (hide) _window?.Away();
        if (giveBack && _before != 0) SetForegroundWindow(_before);
        _before = 0;
        _queue?.TryEnqueue(Refresh);
    }

    // What is typed.

    public static void Type(string text, bool complete = true)
    {
        Typed = text;
        Guess();
        if (!complete) Ending = null;
        _window?.Show(Offers, Picked);
    }

    /// Typed into the field a letter at a time, as a person would (the bench's `launcher type`).
    public static void TypeLetters(string text) => _window?.TypeLetters(text);

    public static void Walk(int step) { Picked = LauncherRules.Walk(Picked, step, Offers.Count); _window?.Show(Offers, Picked); }

    public static void Pick(int? index) { Picked = index is { } i && i >= 0 && i < Offers.Count ? i : null; _window?.Show(Offers, Picked); }

    /// What the field in front's window would offer for the same words, with the pages already open in that
    /// profile's windows first.
    private static void Guess()
    {
        Picked = null;
        if (_target is not { } target || !App.Windows.Contains(target)) _target = target = App.Acting ?? App.Windows.FirstOrDefault()!;
        if (target is null || Typed.Trim().Length == 0) { Offers = Array.Empty<Suggestion>(); Ending = null; return; }
        var open = WindowsOf(target).SelectMany(w => w.OpenPages(Typed)).ToList();
        var (places, ending) = target.PlaceOffers(Typed);
        Offers = LauncherRules.Arrange(open, places);
        Ending = ending;
    }

    /// The windows of the profile the field was raised for, the one in front first.
    private static IEnumerable<MainWindow> WindowsOf(MainWindow target) => new[] { target }.Concat(App.Windows.AsEnumerable().Reverse().Where(w => w != target && w.Profile == target.Profile));

    // Going there.

    public static void Take(int index) { Pick(index); Submit(); }

    /// Enter: the tab that has it, or what was typed opened as a link from another app is, in the window in front
    /// (or a small window over the app you're in, if Settings says so); and Search in front.
    public static void Submit()
    {
        if (!Up || _prefs is not { } prefs) return;
        if (_target is not { } target || !App.Windows.Contains(target)) target = App.Acting ?? App.Windows[0];
        var chosen = Picked is { } p && p < Offers.Count ? Offers[p] : null;
        if (chosen is null && Typed.Trim().Length == 0) { _window?.Refuse(); return; }
        var url = chosen?.Url ?? (Ending is { } ending && Search.Core.Address.UrlFrom(Typed + ending) is { } finished ? finished : target.Destination(Typed));
        // Put away but still on screen while Search's window comes forward: hidden first, the keys would go to some
        // other app, and Windows wouldn't let Search take them back.
        Dismiss(keeping: false, giveBack: false, hide: false);
        Go(chosen, url, target, prefs.LauncherLittle);
        _window?.Away();
    }

    private static void Go(Suggestion? chosen, Uri url, MainWindow target, bool small)
    {
        if (chosen?.Tab is { } id && App.Windows.FirstOrDefault(w => w.Has(id)) is { } owner) { owner.GoTo(id); Foreground(owner.Handle); return; }
        if (small && url.Scheme is "http" or "https") { target.Land(url, small: true); return; }
        foreach (var window in WindowsOf(target))
            if (window.OpenTab(url) is { } open) { window.GoTo(open.Id); Foreground(window.Handle); return; }
        target.Land(url, small: false);
        Foreground(target.Handle);
    }

    /// Windows lets the process that took the key put a window in front; WinUI's Activate alone doesn't ask.
    internal static void Foreground(nint hwnd) => SetForegroundWindow(hwnd);

    public static System.Text.Json.Nodes.JsonObject State() => new()
    {
        ["on"] = _prefs?.Launcher ?? false, ["key"] = _prefs?.LauncherKey ?? "", ["little"] = _prefs?.LauncherLittle ?? false, ["refusal"] = Refusal ?? "",
        ["wants"] = Wants, ["registered"] = Registered, ["up"] = Up, ["shown"] = _window?.Shown ?? false, ["typed"] = Typed, ["field"] = _window?.Text ?? "",
        ["ending"] = Ending ?? "", ["picked"] = Picked ?? -1, ["searchInFront"] = SearchInFront(), ["windowShowing"] = WindowShowing,
        ["offers"] = new System.Text.Json.Nodes.JsonArray(Offers.Select(o => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["url"] = o.Url.AbsoluteUri, ["open"] = o.Tab is not null, ["kind"] = o.Kind.ToString().ToLowerInvariant() }).ToArray())
    };

    // Windows.

    private const uint WmHotKey = 0x0312, EventSystemForeground = 3, EventSystemMinimizeStart = 0x16, EventSystemMinimizeEnd = 0x17;
    private const int ModNoRepeat = 0x4000;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nint id, nint data);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, int modifiers, int key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int process);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, int process, int thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nint id, nint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
}

/// The field and its list, standing over another app: no title bar, over every ordinary window, not in Alt+Tab, on
/// the screen with the pointer a little above the middle, as the Mac's.
internal sealed class LauncherWindow : Window
{
    private const int Width = 680, FieldHeight = 64, RowHeight = 52;
    private readonly TextBox _field = new() { FontSize = 20, PlaceholderText = "Search or type an address", BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private string _typed = "", _known = "";
    private bool _completing;

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public bool Shown => AppWindow.IsVisible;
    public string Text => _field.Text;

    public LauncherWindow()
    {
        Title = "Search";
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        var root = new StackPanel { Padding = new Thickness(16, 10, 16, 10), Children = { _field, _list } };
        Content = root;
        _field.TextChanged += (_, _) => OnTyped();
        _field.PreviewKeyDown += OnKey;
        _list.ItemClick += (_, e) => { if (_list.Items.IndexOf(e.ClickedItem) is >= 0 and var at) Launcher.Take(at); };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && Launcher.Up) Launcher.Dismiss(giveBack: false); };
        AppWindow.Closing += (_, e) => { e.Cancel = true; Launcher.Dismiss(); };
    }

    public void Present(string typed, IReadOnlyList<Suggestion> offers)
    {
        _completing = true;
        _field.Text = _typed = _known = typed;
        _field.SelectAll();
        _completing = false;
        Show(offers, null);
        var area = DisplayArea.GetFromPoint(Cursor(), DisplayAreaFallback.Nearest).WorkArea;
        var scale = Scale;
        AppWindow.Move(new PointInt32(area.X + (area.Width - (int)(Width * scale)) / 2, area.Y + (int)(area.Height * LauncherRules.Drop)));
        Activate();
        Launcher.Foreground(Handle);
        DispatcherQueue.TryEnqueue(() => _field.Focus(FocusState.Programmatic));
    }

    public void Away() { AppWindow.Hide(); _completing = true; _field.Text = ""; _completing = false; _list.Items.Clear(); }

    private double Scale => Launcher.GetDpiForWindow(Handle) / 96.0;

    /// The list as the launcher has it, and the window as tall as what it holds.
    public void Show(IReadOnlyList<Suggestion> offers, int? picked)
    {
        if (_list.Items.Count != offers.Count || !offers.Select((o, i) => (_list.Items[i] as FrameworkElement)?.Tag == (object)o).All(same => same))
        {
            _list.Items.Clear();
            foreach (var offer in offers) _list.Items.Add(Row(offer));
        }
        _list.SelectedIndex = picked ?? -1;
        if (picked is { } at) _list.ScrollIntoView(_list.Items[at]);
        _list.Visibility = offers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = Math.Min(offers.Count, 8);
        AppWindow.Resize(new SizeInt32((int)(Width * Scale), (int)((FieldHeight + (rows > 0 ? rows * RowHeight + 12 : 0)) * Scale)));
    }

    private static FrameworkElement Row(Suggestion offer)
    {
        var said = offer.Kind switch { SuggestionKind.Open => "Switch to tab", SuggestionKind.Bookmark => "Bookmark", _ => "" };
        var grid = new Grid { Tag = offer, Height = RowHeight - 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = offer.Kind == SuggestionKind.Search ? offer.Title + ": " + offer.Key : offer.Title.Length > 0 ? offer.Title : offer.Key, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = offer.Kind == SuggestionKind.Search ? "" : offer.Key, FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis }
        } };
        grid.Children.Add(words);
        var tag = new TextBlock { Text = said, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(tag, 1);
        grid.Children.Add(tag);
        return grid;
    }

    /// Enter on nothing: the field shakes its head.
    public void Refuse() => _field.Focus(FocusState.Programmatic);

    private void OnTyped()
    {
        if (_completing || _field.Text == _known) return;
        var text = _field.Text;
        _known = text;
        // Finished only when a letter was added at the end; deleting must be able to take a completion away.
        var forward = text.Length > _typed.Length && text.StartsWith(_typed, StringComparison.Ordinal) && _field.SelectionStart == text.Length;
        _typed = text;
        Launcher.Type(text, forward);
        if (!forward || Launcher.Ending is not { Length: > 0 } rest) return;
        _completing = true;
        _field.Text = text + rest;
        _field.Select(text.Length, rest.Length);
        _known = _field.Text;
        _completing = false;
    }

    /// Typed as a person would, a letter at a time (the bench's `launcher type`).
    public void TypeLetters(string text)
    {
        foreach (var letter in text)
        {
            var at = _field.SelectionStart;
            _field.Text = _field.Text.Remove(at, _field.SelectionLength).Insert(at, letter.ToString());
            _field.SelectionStart = at + 1;
            _field.SelectionLength = 0;
            OnTyped();
        }
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape: Launcher.Dismiss(); break;
            case VirtualKey.W when control: Launcher.Dismiss(); break;
            case VirtualKey.Down: Launcher.Walk(1); break;
            case VirtualKey.Up: Launcher.Walk(-1); break;
            case VirtualKey.Enter: Launcher.Submit(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static PointInt32 Cursor() { GetCursorPos(out var point); return point; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
}
