using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Search.Core;
using Search.Engine;

namespace Search.App;

// A peek at a link, Arc's way (Peek.swift): Shift+click opens its page in a panel over the one you're reading, which
// stays where it was underneath. Esc, a click beside the panel or its cross puts it away; "Open as tab" keeps it, as a
// tab beside this one, loaded as it is.
public sealed partial class MainWindow
{
    private Tab? _peek;
    private Tab? _peekFrom;

    private void WirePeek()
    {
        _pages.PeekHost = PeekStage;
        _pages.AddScript(Scripts.Read("peek.js"));
        _pages.Message += (tab, kind, body) =>
        {
            if (kind != "peek" || tab != _browser.Selected || !body.TryGetProperty("url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var link)) return;
            DispatcherQueue.TryEnqueue(() => _ = Peek(tab, link));
        };
        _pages.TitleChanged += (tab, title) => { if (tab == _peek) PeekSite.Text = PeekLine(tab); };
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected) && _peek is not null && _browser.Selected != _peekFrom) ClosePeek(); };
    }

    private async Task Peek(Tab from, Uri link)
    {
        ClosePeek();
        var peek = new Tab { Peek = true, Private = from.Private, Url = link };
        _peek = peek;
        _peekFrom = from;
        PeekSite.Text = PeekLine(peek);
        Peeking.Visibility = Visibility.Visible;
        var view = await _pages.Ensure(peek);
        if (_peek == peek) view.Focus(FocusState.Programmatic);
    }

    private static string PeekLine(Tab peek) => peek.Url is { } url ? Search.Core.Address.WithoutWww(url.Host) + (peek.Title.Length > 0 ? " · " + peek.Title : "") : "";

    private void ClosePeek()
    {
        if (_peek is not { } peek) return;
        _peek = null;
        Peeking.Visibility = Visibility.Collapsed;
        _pages.Sleep(peek);
        if (_browser.Selected is { Url: not null } tab) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    /// Kept: a tab of its own beside the one it was opened from, its page as it is.
    private void KeepPeek()
    {
        if (_peek is not { } peek) return;
        _peek = null;
        Peeking.Visibility = Visibility.Collapsed;
        _pages.Keep(peek);
        _browser.Insert(peek, _peekFrom is { } from && _browser.Tabs.IndexOf(from) is var at and >= 0 ? at + 1 : _browser.Ordinary.Count());
        _browser.Selected = peek;
    }

    private void OnPeekScrim(object sender, TappedRoutedEventArgs e) => ClosePeek();
    private void OnPeekClose(object sender, RoutedEventArgs e) => ClosePeek();
    private void OnPeekKeep(object sender, RoutedEventArgs e) => KeepPeek();
}
