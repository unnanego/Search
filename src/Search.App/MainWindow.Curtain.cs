using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Search.Engine;

namespace Search.App;

// Taking things off a page and keeping them off (Curtain.swift, Hidden.swift), your own style for a site
// (SiteStyles.swift), and reading mode (Reader.swift), in the window. The page side is Curtain.cs and Reader.cs.
public sealed partial class MainWindow
{
    private readonly Hidden _hidden = new();
    private readonly SiteStyles _styles = new();
    private readonly Downloads _downloads = new();
    private Tab? _picking;

    private void WireCurtain()
    {
        _styles.On = _prefs.SiteStyles;
        _prefs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Prefs.SiteStyles)) return;
            _styles.On = _prefs.SiteStyles;
            foreach (var host in _browser.Tabs.Where(t => t.Live).Select(t => Hidden.Host(t.Url)).OfType<string>().Distinct().ToList()) _ = RedressHost(host);
        };
        _pages.AddScript(Curtain.DocumentStartScript);
        Curtain.Follow(_pages, _browser, (_, url) => Dress(url));
        _pages.Message += (tab, kind, body) => { if (Curtain.TryHear(kind, body, out var heard)) DispatcherQueue.TryEnqueue(() => OnCurtain(tab, heard)); };
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) StopPicking(); };
    }

    private CurtainDress Dress(Uri? url) => new(_hidden.Css(Hidden.Host(url)), _styles.On ? _styles.Css(SiteStyles.Host(url)) : null);

    /// Ctrl+Shift+H: the next click on the page takes what's under it off, for good on this site. Again, or Esc, stops.
    private void ToggleHiding()
    {
        if (_picking is not null) { StopPicking(); return; }
        if (_browser.Selected is not { Live: true, Url: not null } tab) return;
        _picking = tab;
        _ = Curtain.StartPicking(_pages, tab);
        Notice("Click what you want gone. Esc when you're done; Ctrl+Z puts the last one back.", stay: true);
    }

    private void StopPicking()
    {
        if (_picking is not { } tab) return;
        _picking = null;
        _ = Curtain.StopPicking(_pages, tab);
        HideNotice();
    }

    private void OnCurtain(Tab tab, CurtainHeard heard)
    {
        var host = Hidden.Host(tab.Url);
        switch (heard.Said)
        {
            case CurtainSaid.Chosen when host is not null:
                // A private tab's choices last only in that tab: the page is dressed, nothing is kept.
                if (!tab.Private) _hidden.Hide(heard.Selector, heard.Label, heard.Note, host);
                _ = Redress(tab, tab.Private ? _hidden.Css(host) + Curtain.Sheet(new[] { heard.Selector }) : null);
                Notice($"{Label(heard.Label)} hidden. Ctrl+Z puts it back.", stay: _picking == tab);
                break;
            case CurtainSaid.Undo when host is not null:
                if (_hidden.Undo(host) is { } back) { _ = Redress(tab); Notice($"{Label(back.Label)} is back.", stay: _picking == tab); }
                break;
            case CurtainSaid.Ended:
                if (_picking == tab) { _picking = null; HideNotice(); }
                break;
            case CurtainSaid.Trouble:
                Notice("Couldn't hide that: " + heard.Trouble);
                break;
        }
    }

    private static string Label(string label) => label.Length > 0 ? char.ToUpperInvariant(label[0]) + label[1..] : "It";

    /// The page dressed again as the site's choices now say, now and for its next documents.
    private async Task Redress(Tab tab, string? hiding = null)
    {
        var dress = Dress(tab.Url);
        await Curtain.Arm(_pages, tab, tab.Url, hiding ?? dress.Hiding, dress.Style);
        await Curtain.Apply(_pages, tab, hiding ?? dress.Hiding);
        await Curtain.Style(_pages, tab, dress.Style);
    }

    /// Every live tab on a host dressed again, after a change made for that host.
    private async Task RedressHost(string host)
    {
        foreach (var tab in _browser.Tabs.Where(t => t.Live && Hidden.Host(t.Url) == host).ToList()) await Redress(tab);
    }

    // The hidden panel (Ctrl+Shift+U): what you've taken off this site, a hover shows each, a click puts it back.
    private List<PanelRow> HiddenRows(string words) =>
        _hidden.Veils(Hidden.Host(_browser.Selected?.Url))
            .Where(v => words.Length == 0 || v.Label.Contains(words, StringComparison.OrdinalIgnoreCase) || v.Selector.Contains(words, StringComparison.OrdinalIgnoreCase))
            .Select(v => new PanelRow { Item = v, Glyph = "", Title = Label(v.Label), Detail = v.Note ?? v.Selector, ActionGlyph = "", ActionName = "Put back" }).ToList();

    private void RestoreVeil(Veil veil)
    {
        if (Hidden.Host(_browser.Selected?.Url) is not { } host) return;
        _hidden.Restore(veil, host);
        _ = RedressHost(host);
        FillPanel();
    }

    private void RestoreAllVeils()
    {
        if (Hidden.Host(_browser.Selected?.Url) is not { } host) return;
        _hidden.RestoreAll(host);
        _ = RedressHost(host);
        FillPanel();
    }

    /// Your own CSS for the site in front, as Arc's Boosts: written here, on every page of the site from then on.
    private async Task EditSiteStyle()
    {
        if (_browser.Selected is not { Url: { } url } tab || SiteStyles.Host(url) is not { } host) return;
        var box = new TextBox { AcceptsReturn = true, Text = _styles.Written(host), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), Height = 260, Width = 460, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "body { font-size: 18px; }" };
        var typing = DispatcherQueue.CreateTimer();
        typing.Interval = TimeSpan.FromMilliseconds(250);
        typing.IsRepeating = false;
        typing.Tick += (_, _) => { _ = Curtain.Style(_pages, tab, box.Text); };
        box.TextChanged += (_, _) => { typing.Stop(); typing.Start(); };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Your style for {host}", Content = box, PrimaryButtonText = "Keep", SecondaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        var answer = await dialog.ShowAsync();
        typing.Stop();
        if (answer == ContentDialogResult.Primary) _styles.Set(box.Text, host);
        else if (answer == ContentDialogResult.Secondary) _styles.Remove(host);
        await RedressHost(host);
    }

    /// F9: the article, and nothing that was arranged around it; F9 again, the page as it was.
    private async Task ToggleReading()
    {
        if (_browser.Selected is not { Live: true } tab) return;
        var look = _prefs.Theme switch { Theme.Light => ReaderLook.Light, Theme.Dark => ReaderLook.Dark, _ => ReaderLook.System };
        if (await Reader.Toggle(_pages, tab, look, twins: false) == ReaderOutcome.Nothing) Notice("Nothing to read on this page.");
    }

    /// A finished download remembered, for the panel after a restart (Loot.swift).
    private void Remember(Fetch fetch, Uri? from) => _downloads.Add(Keep.Saved(fetch.Path, from));
}
