using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Search.Engine;
using Windows.System;

namespace Search.App;

// Ctrl+F (Find.swift, PageFind.swift): Chromium's own find, through WebView2's Find API, with our bar instead of its
// dialog. A count, the match you're on, Enter and F3 for the next, Shift for the one before.
public sealed partial class MainWindow
{
    private CoreWebView2? _finding;
    /// The find object whose events we listen to, held so its wrapper isn't collected with our subscriptions.
    private CoreWebView2Find? _finder;
    /// One find at a time: each letter typed asks again, and overlapping starts raced each other into "2 of 3".
    private bool _findRunning, _findAgain;
    /// The word last searched for: TextChanged arrives late, and the same word again must not start over.
    private string? _searched;

    private void WireFind()
    {
        FindText.TextChanged += (_, _) => _ = StartFind();
        FindText.PreviewKeyDown += (_, e) => { if (FindKey(e.Key, Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))) e.Handled = true; };
    }

    private bool FindKey(VirtualKey key, bool shift)
    {
        if (key == VirtualKey.Enter) { _ = FindStep(!shift); return true; }
        if (key == VirtualKey.Escape) { CloseFind(); return true; }
        return false;
    }

    private void OpenFind()
    {
        if (_browser.Selected is not { Live: true }) return;
        _keysAsked++;
        FindBar.Visibility = Visibility.Visible;
        FindText.Focus(FocusState.Keyboard);
        FindText.SelectAll();
        if (FindText.Text.Length > 0) _ = StartFind();
    }

    private async Task StartFind()
    {
        if (_findRunning) { _findAgain = true; return; }
        _findRunning = true;
        try { do { _findAgain = false; await FindNow(); } while (_findAgain); }
        finally { _findRunning = false; }
    }

    private async Task FindNow()
    {
        if (_browser.Selected is not { } tab || _pages.CoreOf(tab) is not { } core) return;
        if (FindText.Text.Length == 0) { core.Find.Stop(); FindCount.Text = ""; _searched = null; return; }
        if (core == _finding && FindText.Text == _searched) return;
        if (_finding != core)
        {
            _finding?.Find.Stop();
            _finding = core;
            _finder = core.Find;
            _finder.MatchCountChanged += (_, _) => Count(core);
            _finder.ActiveMatchIndexChanged += (_, _) => Count(core);
        }
        // A new word starts again from the top; without this each letter typed went on from the last match.
        core.Find.Stop();
        var options = (await Web.Environment).CreateFindOptions();
        options.FindTerm = FindText.Text;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;
        _searched = options.FindTerm;
        await core.Find.StartAsync(options);
        // No match raises no change of count when there were matches before.
        Count(core);
    }

    private void Count(CoreWebView2 core)
    {
        if (core != _finding) return;
        var count = core.Find.MatchCount;
        // ActiveMatchIndex is already counted from 1.
        FindCount.Text = count == 0 ? "None" : $"{Math.Max(1, core.Find.ActiveMatchIndex)} of {count}";
    }

    private async Task FindStep(bool forward)
    {
        if (FindBar.Visibility != Visibility.Visible) { OpenFind(); return; }
        if (_finding is null) { await StartFind(); return; }
        if (forward) _finding.Find.FindNext(); else _finding.Find.FindPrevious();
    }

    private void CloseFind()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        _finding?.Find.Stop();
        _finding = null;
        _searched = null;
        FindCount.Text = "";
        FindBar.Visibility = Visibility.Collapsed;
        if (_browser.Selected is { } tab) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => _ = FindStep(true);
    private void OnFindPrevious(object sender, RoutedEventArgs e) => _ = FindStep(false);
    private void OnFindClose(object sender, RoutedEventArgs e) => CloseFind();
}
