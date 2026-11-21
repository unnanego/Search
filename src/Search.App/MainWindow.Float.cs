using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;

namespace Search.App;

// Ctrl+Shift+P: the video lifted out of the page into a window above everything (Float.swift, NativePiP.swift).
// Chromium's own picture in picture does it, and Chromium wants a user's gesture for it: the key press is one, and
// the request goes in as one, through DevTools. And Ctrl+K: the open tabs by name, typed to narrow, Enter to go.
public sealed partial class MainWindow
{
    /// The playing video (the biggest, if several) to its own window, or back into the page if one already floats.
    private const string FloatVideo = """
        (async () => {
          if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'back'; }
          const videos = [...document.querySelectorAll('video')].filter(v => v.readyState > 0 && !v.disablePictureInPicture);
          const video = videos.find(v => !v.paused) && videos.filter(v => !v.paused).sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0] || videos[0];
          if (!video) return 'none';
          try { await video.requestPictureInPicture(); return 'floating'; } catch (e) { return 'refused: ' + e.message; }
        })()
        """;

    private async Task ToggleFloat()
    {
        if (_browser.Selected is not { Live: true } tab || _pages.CoreOf(tab) is not { } core) return;
        var answer = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = FloatVideo, userGesture = true, awaitPromise = true, returnByValue = true }));
        var said = JsonNode.Parse(answer)?["result"]?["value"]?.GetValue<string>();
        if (said == "none") Notice("No video on this page.");
        else if (said?.StartsWith("refused") == true) Notice("The page won't let its video float.");
    }

    // Ctrl+K: open tabs by name.
    private void OpenSwitcher()
    {
        SwitcherText.Text = "";
        Switcher.Visibility = Visibility.Visible;
        FillSwitcher();
        SwitcherText.Focus(FocusState.Keyboard);
    }

    private void CloseSwitcher(bool backToPage = true)
    {
        if (Switcher.Visibility != Visibility.Visible) return;
        Switcher.Visibility = Visibility.Collapsed;
        SwitcherRows.ItemsSource = null;
        if (backToPage && _browser.Selected is { Url: not null } tab) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    private void FillSwitcher()
    {
        var words = SwitcherText.Text.Trim();
        var rows = _browser.Ordinary.Where(t => words.Length == 0 || t.Shown.Contains(words, StringComparison.OrdinalIgnoreCase) || (t.Url?.Host.Contains(words, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(t => new PanelRow { Item = t, Glyph = t == _browser.Selected ? "" : "", Title = Shown(t), Detail = t.Url is { } u ? Search.Core.Address.Pretty(u) : "" }).ToList();
        SwitcherRows.ItemsSource = rows;
        SwitcherRows.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    private void WireSwitcher()
    {
        SwitcherText.TextChanged += (_, _) => FillSwitcher();
        SwitcherText.PreviewKeyDown += (_, e) => { if (SwitcherKey(e.Key)) e.Handled = true; };
        SwitcherText.LostFocus += (_, _) => { if (SwitcherRows.FocusState == FocusState.Unfocused) CloseSwitcher(backToPage: false); };
        SwitcherRows.ItemClick += (_, e) => { if (e.ClickedItem is PanelRow { Item: Tab tab }) { CloseSwitcher(backToPage: false); _browser.Selected = tab; } };
    }

    private bool SwitcherKey(Windows.System.VirtualKey key)
    {
        var count = SwitcherRows.Items.Count;
        switch (key)
        {
            case Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up when count > 0:
                SwitcherRows.SelectedIndex = ((SwitcherRows.SelectedIndex + (key == Windows.System.VirtualKey.Down ? 1 : -1)) % count + count) % count;
                SwitcherRows.ScrollIntoView(SwitcherRows.SelectedItem);
                return true;
            case Windows.System.VirtualKey.Enter:
                if (SwitcherRows.SelectedItem is PanelRow { Item: Tab tab }) { CloseSwitcher(backToPage: false); _browser.Selected = tab; }
                return true;
            case Windows.System.VirtualKey.Escape:
                CloseSwitcher();
                return true;
            default:
                return false;
        }
    }
}
