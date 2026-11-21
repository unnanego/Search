using System.Collections.ObjectModel;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Windows.System;

namespace Search.App;

public enum PanelKind { None, History, Downloads, Bookmarks, Passwords, Hidden }

// The panels (Plate.swift, Recall.swift, Loot.swift, Bookmarks.swift's panel): one shape for all of them, over the
// page's right side, each searchable and a key away — Ctrl+H, Ctrl+J, Ctrl+Shift+O — and put away with Esc.
public sealed partial class MainWindow
{
    private PanelKind _panel;
    private readonly ObservableCollection<Fetch> _fetches = new();

    private void WirePanels()
    {
        PanelSearch.TextChanged += (_, _) => FillPanel();
        PanelRows.ItemClick += (_, e) => { if (e.ClickedItem is PanelRow row) TakePanelRow(row); };
        _history.Changed += () => DispatcherQueue.TryEnqueue(() => { if (_panel == PanelKind.History) FillPanel(); });
        _pages.DownloadStarted += (_, operation) =>
        {
            var fetch = new Fetch(operation);
            var from = Uri.TryCreate(operation.Uri, UriKind.Absolute, out var source) ? source : null;
            var tab = _browser.Selected;
            operation.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (operation.State == CoreWebView2DownloadState.Completed && tab is not { Private: true }) Remember(fetch, from); });
            _fetches.Insert(0, fetch);
            operation.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_panel == PanelKind.Downloads) FillPanel(); });
            // A download shows itself once, as it starts; nothing else is opened for you.
            if (_panel == PanelKind.None) OpenPanel(PanelKind.Downloads); else if (_panel == PanelKind.Downloads) FillPanel();
        };
    }

    private void TogglePanel(PanelKind kind) { if (_panel == kind) ClosePanel(); else OpenPanel(kind); }

    private void OpenPanel(PanelKind kind)
    {
        _panel = kind;
        PanelTitle.Text = kind switch { PanelKind.History => "History", PanelKind.Downloads => "Downloads", PanelKind.Passwords => "Passwords", PanelKind.Hidden => "Hidden on this site", _ => "Bookmarks" };
        PanelSearch.Text = "";
        Panel.Visibility = Visibility.Visible;
        FillPanel();
        PanelSearch.Focus(FocusState.Keyboard);
    }

    private void ClosePanel()
    {
        if (_panel == PanelKind.None) return;
        _panel = PanelKind.None;
        Panel.Visibility = Visibility.Collapsed;
        PanelRows.ItemsSource = null;
        if (_browser.Selected is { Url: not null } tab) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    private void FillPanel()
    {
        var words = PanelSearch.Text.Trim();
        List<PanelRow> rows;
        (string Label, bool Shown) foot = ("", false);
        switch (_panel)
        {
            case PanelKind.History:
                rows = _history.Everything(words).Take(500).Select(t => new PanelRow
                {
                    Item = t, Glyph = "", Title = t.Title.Length > 0 ? t.Title : t.Address, Detail = $"{t.Address} · {When(t.Last)}", ActionGlyph = "", ActionName = "Forget"
                }).ToList();
                foot = ("Clear history…", true);
                break;
            case PanelKind.Downloads:
                rows = _fetches.Where(f => words.Length == 0 || f.Title.Contains(words, StringComparison.OrdinalIgnoreCase)).Cast<PanelRow>().ToList();
                // What came before this session, as it was kept; what has since gone from disk says so.
                var now = _fetches.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.AddRange(_downloads.Kept.Where(k => !now.Contains(k.Path) && (words.Length == 0 || k.Name.Contains(words, StringComparison.OrdinalIgnoreCase)))
                    .Select(k => new PanelRow { Item = k, Glyph = "", Title = k.Name, Detail = (k.StillThere ? "" : "Moved or deleted · ") + k.From + " · " + When(k.Date), ActionGlyph = k.StillThere ? "" : "", ActionName = k.StillThere ? "Show in folder" : "Forget" }));
                foot = ("Open the Downloads folder", true);
                break;
            case PanelKind.Passwords:
                rows = PasswordRows(words);
                break;
            case PanelKind.Hidden:
                rows = HiddenRows(words);
                foot = ("Put everything back", rows.Count > 0);
                break;
            default:
                rows = BookmarkRows(words);
                break;
        }
        PanelRows.ItemsSource = rows;
        PanelEmpty.Text = rows.Count > 0 ? "" : words.Length > 0 ? "Nothing matches." : _panel switch { PanelKind.History => "Nowhere yet.", PanelKind.Downloads => "Nothing downloaded yet.", PanelKind.Passwords => "No passwords kept yet.", PanelKind.Hidden => "Nothing hidden on this site.", _ => "No bookmarks yet." };
        PanelFoot.Content = foot.Label;
        PanelFoot.Visibility = foot.Shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// When, the way a list of places says it: a time today, a day this week, a date before that.
    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today ? local.ToString("t") : local.Date > today.AddDays(-7) ? local.ToString("dddd t") : local.ToString("d");
    }

    private void TakePanelRow(PanelRow row)
    {
        switch (row.Item)
        {
            case Trace trace: Go(trace.Url, newTab: false); break;
            case Account account: _ = CopyPassword(account); break;
            case Veil veil: RestoreVeil(veil); break;
            case Keep { StillThere: true } kept: Open(kept.Path); break;
            case null when row is Fetch { State: CoreWebView2DownloadState.Completed } fetch: Open(fetch.Path); break;
            default: TakeBookmarkRow(row); break;
        }
    }

    private void OnPanelRowAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PanelRow row }) return;
        switch (row)
        {
            case { Item: Trace trace }: _history.Forget(trace.Key); FillPanel(); break;
            case { Item: Account account }: _ = ForgetPassword(account); break;
            case { Item: Veil veil }: RestoreVeil(veil); break;
            case { Item: Keep { StillThere: true } kept }: ShowInFolder(kept.Path); break;
            case { Item: Keep kept }: _downloads.Forget(kept); FillPanel(); break;
            case Fetch { State: CoreWebView2DownloadState.InProgress } fetch: fetch.Operation.Cancel(); break;
            case Fetch { State: CoreWebView2DownloadState.Completed } fetch: ShowInFolder(fetch.Path); break;
            default: BookmarkRowAction(row); break;
        }
    }

    private async void OnPanelFoot(object sender, RoutedEventArgs e)
    {
        if (_panel == PanelKind.Downloads) { Open(_fetches.FirstOrDefault() is { } last ? Path.GetDirectoryName(last.Path)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"); return; }
        if (_panel == PanelKind.Hidden) { RestoreAllVeils(); return; }
        if (_panel != PanelKind.History) return;
        var sure = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Clear history?", Content = "Every place you've been is forgotten. Open tabs, bookmarks and passwords stay.", PrimaryButtonText = "Clear", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await sure.ShowAsync() == ContentDialogResult.Primary) { _history.Forget(); FillPanel(); }
    }

    private void OnPanelClose(object sender, RoutedEventArgs e) => ClosePanel();

    private static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
    private static void ShowInFolder(string path) { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (System.ComponentModel.Win32Exception) { } }
}
