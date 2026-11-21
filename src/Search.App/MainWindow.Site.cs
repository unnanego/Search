using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Search.Core;

namespace Search.App;

// What belongs to a site (SiteCard.swift, PageQuestions.swift, Notifications.swift): the questions a page may ask and
// the answers it gets, the site card under the address, and the notifications a site sends.
public sealed partial class MainWindow
{
    private readonly Queue<(Tab Tab, CoreWebView2PermissionRequestedEventArgs Ask, Windows.Foundation.Deferral Wait)> _asks = new();
    private (Tab Tab, CoreWebView2PermissionRequestedEventArgs Ask, Windows.Foundation.Deferral Wait)? _asking;
    /// What a test run would have posted, newest last, for the bench: a real notification lands on whoever's screen.
    private readonly List<(string Site, string Title, string Body)> _posted = new();
    private bool _notificationsReady;

    private void WireSite()
    {
        _pages.PermissionAsked += (tab, ask, wait) =>
        {
            // A private tab is never asked for notifications and keeps nothing: it's simply refused.
            if (tab.Private && ask.PermissionKind == CoreWebView2PermissionKind.Notifications) { ask.State = CoreWebView2PermissionState.Deny; wait.Complete(); return; }
            _asks.Enqueue((tab, ask, wait));
            NextAsk();
        };
        _pages.Notified += (tab, note) => Post(tab, note);
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) NextAsk(); };
    }

    /// The question in front is the one for the tab in front; another tab's waits until you go to it.
    private void NextAsk()
    {
        if (_asking is { } now && now.Tab != _browser.Selected) { Asking.Visibility = Visibility.Collapsed; _asks.Enqueue(now); _asking = null; }
        if (_asking is not null) return;
        var count = _asks.Count;
        for (var i = 0; i < count; i++)
        {
            var next = _asks.Dequeue();
            if (!_browser.Tabs.Contains(next.Tab)) { next.Ask.State = CoreWebView2PermissionState.Deny; next.Wait.Complete(); continue; }
            if (next.Tab != _browser.Selected) { _asks.Enqueue(next); continue; }
            _asking = next;
            var site = Uri.TryCreate(next.Ask.Uri, UriKind.Absolute, out var u) ? Search.Core.Address.WithoutWww(u.Host) : next.Ask.Uri;
            AskingText.Text = $"{site} would like {Wants(next.Ask.PermissionKind)}.";
            AskingKeep.Visibility = next.Tab.Private ? Visibility.Collapsed : Visibility.Visible;
            AskingKeep.IsChecked = true;
            Asking.Visibility = Visibility.Visible;
            return;
        }
        Asking.Visibility = Visibility.Collapsed;
    }

    private static string Wants(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => "to use your camera",
        CoreWebView2PermissionKind.Microphone => "to use your microphone",
        CoreWebView2PermissionKind.Geolocation => "to know where you are",
        CoreWebView2PermissionKind.Notifications => "to send you notifications",
        CoreWebView2PermissionKind.ClipboardRead => "to read what you've copied",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "to download several files",
        CoreWebView2PermissionKind.WindowManagement => "to place windows on your screens",
        CoreWebView2PermissionKind.LocalFonts => "to use the fonts on this PC",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "to control your MIDI devices",
        _ => "a permission (" + kind + ")"
    };

    private void OnAskingAnswer(object sender, RoutedEventArgs e) => Answer(sender is FrameworkElement { Tag: "allow" });

    private void Answer(bool allow)
    {
        if (_asking is not { } now) return;
        _asking = null;
        now.Ask.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        now.Ask.SavesInProfile = !now.Tab.Private && AskingKeep.IsChecked == true;
        now.Wait.Complete();
        Asking.Visibility = Visibility.Collapsed;
        NextAsk();
    }

    private void Post(Tab tab, CoreWebView2Notification note)
    {
        var site = tab.Url is { } url ? Search.Core.Address.WithoutWww(url.Host) : "";
        note.ReportShown();
        if (Store.Testing) { _posted.Add((site, note.Title, note.Body)); return; }
        try
        {
            if (!_notificationsReady)
            {
                AppNotificationManager.Default.NotificationInvoked += (_, args) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (args.Arguments.TryGetValue("tab", out var id) && _browser.Ordinary.FirstOrDefault(t => t.ShortId == id) is { } from) { _browser.Selected = from; Activate(); }
                });
                AppNotificationManager.Default.Register();
                _notificationsReady = true;
            }
            var toast = new AppNotificationBuilder().AddArgument("tab", tab.ShortId).AddText(note.Title).AddText(note.Body).SetAttributionText(site).BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { App.Log(e); }
    }

    // The site card: under the address, for the tab in front.
    private async void OnSiteButton(object sender, RoutedEventArgs e)
    {
        if (_browser.Selected is not { Url: { } url } tab) return;
        var host = url.Host;
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(new TextBlock { Text = Search.Core.Address.WithoutWww(host), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        var secure = url.Scheme == "https";
        panel.Children.Add(new TextBlock { Text = secure ? "The connection is private." : url.Scheme == "http" ? "The connection isn't private: what you send here can be read on the way." : url.Scheme, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        if (url.Scheme is "http" or "https")
        {
            var shield = new ToggleSwitch { Header = "Block trackers and ads here", IsOn = _prefs.Shield && !_prefs.ShieldPausedOn(host), IsEnabled = _prefs.Shield };
            shield.Toggled += (_, _) => { _prefs.PauseShield(host, !shield.IsOn); Page(TabCommand.Reload); };
            panel.Children.Add(shield);
            if (_pages.CoreOf(tab) is { } core) await AddPermissions(panel, core, url);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = "Copy address" };
        copy.Click += (_, _) => CopyAddress();
        var print = new Button { Content = "Print…" };
        print.Click += (_, _) => _pages.CoreOf(tab)?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        actions.Children.Add(copy);
        actions.Children.Add(print);
        panel.Children.Add(actions);
        var page = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var hide = new Button { Content = "Hide something…" };
        var style = new Button { Content = "Your style…" };
        var read = new Button { Content = "Read" };
        page.Children.Add(hide);
        page.Children.Add(style);
        page.Children.Add(read);
        panel.Children.Add(page);
        var flyout = new Flyout { Content = panel };
        hide.Click += (_, _) => { flyout.Hide(); ToggleHiding(); };
        style.Click += (_, _) => { flyout.Hide(); _ = EditSiteStyle(); };
        read.Click += (_, _) => { flyout.Hide(); _ = ToggleReading(); };
        flyout.ShowAt(SiteButton);
    }

    /// What this site may use: what's been answered for it, each changeable; nothing listed for a site never asked.
    private static async Task AddPermissions(StackPanel panel, CoreWebView2 core, Uri url)
    {
        var origin = url.GetLeftPart(UriPartial.Authority);
        IReadOnlyList<CoreWebView2PermissionSetting> settings;
        try { settings = await core.Profile.GetNonDefaultPermissionSettingsAsync(); } catch (System.Runtime.InteropServices.COMException) { return; }
        var mine = settings.Where(s => s.PermissionOrigin.TrimEnd('/').Equals(origin, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return;
        panel.Children.Add(new TextBlock { Text = "This site may", Opacity = 0.75 });
        foreach (var setting in mine)
        {
            var choice = new ComboBox { Header = Capitalised(Wants(setting.PermissionKind)), ItemsSource = new[] { "Ask", "Allow", "Don't allow" }, MinWidth = 280 };
            choice.SelectedIndex = setting.PermissionState switch { CoreWebView2PermissionState.Allow => 1, CoreWebView2PermissionState.Deny => 2, _ => 0 };
            choice.SelectionChanged += async (_, _) => await core.Profile.SetPermissionStateAsync(setting.PermissionKind, setting.PermissionOrigin,
                choice.SelectedIndex switch { 1 => CoreWebView2PermissionState.Allow, 2 => CoreWebView2PermissionState.Deny, _ => CoreWebView2PermissionState.Default });
            panel.Children.Add(choice);
        }
    }

    private static string Capitalised(string wants) => wants.StartsWith("to ") ? char.ToUpperInvariant(wants[3]) + wants[4..] : wants;
}
