using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Search.Core;
using Windows.System;

namespace Search.App;

// What a window gives the launcher (Launcher.cs): its open pages for words, the tab that has an address, a tab brought
// forward, and a page landed as one from another app would.
public sealed partial class MainWindow
{
    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// On screen: shown and not minimised.
    internal bool Showing => AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// The pages open in this window's space that match, as the field offers them; never a private tab, nor one a
    /// script or an agent opened.
    internal IEnumerable<Suggestion> OpenPages(string typed)
    {
        var needle = typed.Trim();
        return Openable().Where(t => t.Shown.Contains(needle, StringComparison.OrdinalIgnoreCase) || t.Url!.AbsoluteUri.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(t => new Suggestion(Search.Core.Address.Editable(t.Url!), t.Shown, t.Url!, SuggestionKind.Open) { Tab = t.Id });
    }

    /// The tab here that already has `url`, so Enter goes there rather than opening it twice.
    internal Tab? OpenTab(Uri url) => Openable().FirstOrDefault(t => Pins.SamePage(url.AbsoluteUri, t.Url!.AbsoluteUri));

    private IEnumerable<Tab> Openable() => _browser.Ordinary.Where(t => !t.Private && !t.Agent && t.Url is not null);

    internal bool Has(Guid tab) => _browser.Ordinary.Any(t => t.Id == tab);

    /// Settings › From any app: the switch, the key (a click, then the key you want), whether what it finds opens in a
    /// small window, and why Windows wouldn't give the key, when it wouldn't.
    private StackPanel LauncherSettings()
    {
        var on = new ToggleSwitch { Header = "Open the address field from any app", IsOn = _prefs.Launcher };
        var key = new Button { MinWidth = 160, IsEnabled = _prefs.Launcher };
        var suggested = new HyperlinkButton { Content = "Use " + LauncherKey.Suggested.Display, IsEnabled = _prefs.Launcher };
        var refusal = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        var little = new ToggleSwitch { Header = "Open what it finds in a small window", IsOn = _prefs.LauncherLittle, IsEnabled = _prefs.Launcher };
        var note = new TextBlock { Text = "Press the key over any app and type: it finds your bookmarks, where you've been and the tabs you have open. Enter opens it in Search. In Search's own windows the key is the page's.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var recording = false;
        void Say()
        {
            key.Content = recording ? "Press a key…" : LauncherKey.Parse(_prefs.LauncherKey)?.Display ?? "No key";
            suggested.Visibility = _prefs.LauncherKey == LauncherKey.Suggested.Display ? Visibility.Collapsed : Visibility.Visible;
            refusal.Text = Launcher.Refusal ?? "";
            refusal.Visibility = Launcher.Refusal is null ? Visibility.Collapsed : Visibility.Visible;
        }
        void Record(bool listening) { recording = listening; Launcher.Recording = listening; Say(); }
        on.Toggled += (_, _) => { _prefs.Launcher = on.IsOn; key.IsEnabled = suggested.IsEnabled = little.IsEnabled = on.IsOn; Say(); };
        little.Toggled += (_, _) => _prefs.LauncherLittle = little.IsOn;
        suggested.Click += (_, _) => { _prefs.LauncherKey = LauncherKey.Suggested.Display; Say(); };
        key.Click += (_, _) => Record(!recording);
        key.LostFocus += (_, _) => { if (recording) Record(false); };
        key.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            if (e.Key == VirtualKey.Escape) { Record(false); return; }
            if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftShift or VirtualKey.RightShift) return;
            static bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var modifiers = (Down(VirtualKey.Control) ? LauncherKey.Control : 0) | (Down(VirtualKey.Menu) ? LauncherKey.Alt : 0) | (Down(VirtualKey.Shift) ? LauncherKey.Shift : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? LauncherKey.Win : 0);
            var chosen = new LauncherKey(modifiers, (int)e.Key);
            recording = false;
            Launcher.Recording = false;
            _prefs.LauncherKey = chosen.Display;
            Say();
        };
        Action changed = () => DispatcherQueue.TryEnqueue(Say);
        Launcher.Changed += changed;
        var panel = new StackPanel { Spacing = 8, Children = { on, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { key, suggested } }, refusal, little, note } };
        panel.Unloaded += (_, _) => { Launcher.Changed -= changed; if (recording) Record(false); };
        Say();
        return panel;
    }

    /// A tab of this window, in front, and the window too.
    internal void GoTo(Guid id)
    {
        if (_browser.Ordinary.FirstOrDefault(t => t.Id == id) is not { } tab) return;
        Bring();
        _browser.Selected = tab;
        _ = Front(tab, givePage: true);
    }
}
