using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Search.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Search.App;

// The keys (the README's Keyboard table, in Windows' words: ⌘ is Ctrl, ⌥ is Alt). Where the Mac's key means
// something else everywhere on Windows, Windows wins: Ctrl+D stays free for bookmarks, so Duplicate is Ctrl+Shift+K
// as in Chrome and Edge, and Ctrl+Shift+R is a reload past the cache, as in every Windows browser.
public sealed partial class MainWindow
{
    private const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control, Shift = VirtualKeyModifiers.Shift, Alt = VirtualKeyModifiers.Menu, CtrlShift = Ctrl | Shift;

    private void WireKeys()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Key(VirtualKey.L, Ctrl, FocusField);
        Key(VirtualKey.F6, VirtualKeyModifiers.None, FocusField);
        Key(VirtualKey.T, Ctrl, () => NewTab());
        Key(VirtualKey.N, CtrlShift, () => NewTab(@private: true));
        Key(VirtualKey.W, Ctrl, () => { if (_browser.Selected is { } tab) Close(tab); });
        Key(VirtualKey.F4, Ctrl, () => { if (_browser.Selected is { } tab) Close(tab); });
        Key(VirtualKey.T, CtrlShift, () => _browser.Reopen());
        Key(VirtualKey.Tab, Ctrl, () => _browser.Step(1));
        Key(VirtualKey.Tab, CtrlShift, () => _browser.Step(-1));
        Key(VirtualKey.PageDown, Ctrl, () => _browser.Step(1));
        Key(VirtualKey.PageUp, Ctrl, () => _browser.Step(-1));
        for (var n = 1; n <= 9; n++) { var number = n; Key(VirtualKey.Number0 + n, Ctrl, () => _browser.SelectNumber(number)); }
        for (var n = 1; n <= 9; n++) { var number = n; Key(VirtualKey.Number0 + n, Alt, () => GoToSpaceNumber(number)); }
        Key(VirtualKey.Left, Alt, () => Page(TabCommand.Back));
        Key(VirtualKey.Right, Alt, () => Page(TabCommand.Forward));
        Key((VirtualKey)219, Ctrl, () => Page(TabCommand.Back));    // Ctrl+[
        Key((VirtualKey)221, Ctrl, () => Page(TabCommand.Forward)); // Ctrl+]
        Key(VirtualKey.GoBack, VirtualKeyModifiers.None, () => Page(TabCommand.Back));
        Key(VirtualKey.GoForward, VirtualKeyModifiers.None, () => Page(TabCommand.Forward));
        Key(VirtualKey.R, Ctrl, () => Page(TabCommand.Reload));
        Key(VirtualKey.F5, VirtualKeyModifiers.None, () => Page(TabCommand.Reload));
        Key(VirtualKey.R, CtrlShift, () => Page(TabCommand.ReloadFromOrigin));
        Key(VirtualKey.F5, Ctrl, () => Page(TabCommand.ReloadFromOrigin));
        Key(VirtualKey.F, Ctrl, OpenFind);
        Key(VirtualKey.F3, VirtualKeyModifiers.None, () => _ = FindStep(forward: true));
        Key(VirtualKey.F3, Shift, () => _ = FindStep(forward: false));
        Key(VirtualKey.K, CtrlShift, () => { if (_browser.Selected is { } tab) _browser.Duplicate(tab); });
        Key(VirtualKey.C, CtrlShift, CopyAddress);
        Key(VirtualKey.V, CtrlShift, () => _ = PasteAndGo());
        Key(VirtualKey.S, CtrlShift, () => _prefs.TabsOnSide = !_prefs.TabsOnSide);
        Key(VirtualKey.S, Ctrl, () => { if (_prefs.TabsOnSide) _prefs.Folded = !_prefs.Folded; });
        Key((VirtualKey)188, Ctrl, () => _ = OpenSettings()); // Ctrl+,
        Key(VirtualKey.F11, VirtualKeyModifiers.None, () => SetFullScreen(!_fullScreen));
        Key(VirtualKey.F12, VirtualKeyModifiers.None, () => { if (_browser.Selected is { } tab) _pages.CoreOf(tab)?.OpenDevToolsWindow(); });
        Key(VirtualKey.I, CtrlShift, () => { if (_browser.Selected is { } tab) _pages.CoreOf(tab)?.OpenDevToolsWindow(); });
        Key(VirtualKey.P, Ctrl, () => { if (_browser.Selected is { } tab) _pages.CoreOf(tab)?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser); });
        Key(VirtualKey.H, Ctrl, () => TogglePanel(PanelKind.History));
        Key(VirtualKey.J, Ctrl, () => TogglePanel(PanelKind.Downloads));
        Key(VirtualKey.O, CtrlShift, () => TogglePanel(PanelKind.Bookmarks));
        Key(VirtualKey.L, CtrlShift, () => TogglePanel(PanelKind.Passwords));
        Key(VirtualKey.D, Ctrl, ToggleBookmark);
        Key(VirtualKey.P, CtrlShift, () => _ = ToggleFloat());
        Key(VirtualKey.K, Ctrl, OpenSwitcher);
        Key((VirtualKey)220, Ctrl, SplitCurrent);           // Ctrl+\
        Key((VirtualKey)220, CtrlShift, SeparateCurrent);   // Ctrl+Shift+\
        Key(VirtualKey.Left, Ctrl | Alt, () => _browser.StepPane(-1));
        Key(VirtualKey.Right, Ctrl | Alt, () => _browser.StepPane(1));
        Key(VirtualKey.H, CtrlShift, ToggleHiding);
        Key(VirtualKey.U, CtrlShift, () => TogglePanel(PanelKind.Hidden));
        Key(VirtualKey.F9, VirtualKeyModifiers.None, () => _ = ToggleReading());
        Key(VirtualKey.Escape, VirtualKeyModifiers.None, PutAway, always: false);
        // Tab is XAML's for moving focus before any accelerator sees it, so Ctrl+Tab is taken on the way down.
        Root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Tab || !Down(VirtualKey.Control)) return;
            _browser.Step(Down(VirtualKey.Shift) ? -1 : 1);
            e.Handled = true;
        };
    }

    /// A key's name as Press reads it and the page listener writes it: "ctrl+shift+t", "f5", "alt+left".
    private static string Name(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var name = key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString().ToLowerInvariant(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)key - (int)VirtualKey.Number0).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F24 => "f" + ((int)key - (int)VirtualKey.F1 + 1),
            VirtualKey.Escape => "esc", (VirtualKey)188 => ",", (VirtualKey)219 => "[", (VirtualKey)221 => "]", (VirtualKey)220 => "\\",
            _ => key.ToString().ToLowerInvariant()
        };
        return (modifiers.HasFlag(Ctrl) ? "ctrl+" : "") + (modifiers.HasFlag(Shift) ? "shift+" : "") + (modifiers.HasFlag(Alt) ? "alt+" : "") + name;
    }

    /// The keys a page passes to the window: every one the window answers, but Esc, which is the page's.
    private IReadOnlyCollection<string> PageKeys() => _keys.Keys.Where(k => k.Item1 != VirtualKey.Escape).Select(k => Name(k.Item1, k.Item2)).Concat(new[] { "ctrl+tab", "ctrl+shift+tab" }).ToHashSet();

    private static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// Every key the window answers, for the bench's test-only `press`.
    private readonly Dictionary<(VirtualKey, VirtualKeyModifiers), Action> _keys = new();

    private void Key(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool always = true)
    {
        _keys[(key, modifiers)] = action;
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { action(); e.Handled = always; };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private void Page(TabCommand command) => _browser.Selected?.Do(command);

    /// A key pressed as the window would take it, from its name ("ctrl+shift+t", "enter", "f3"): the field's own keys
    /// while it has focus, Ctrl+Tab, then the window's keys. False when nothing answers it.
    private bool Press(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+');
        var modifiers = VirtualKeyModifiers.None;
        foreach (var part in parts[..^1]) modifiers |= part switch { "ctrl" => Ctrl, "shift" => Shift, "alt" => Alt, _ => VirtualKeyModifiers.None };
        VirtualKey? key = parts[^1] switch
        {
            [var c] when char.IsAsciiLetterUpper(char.ToUpperInvariant(c)) => (VirtualKey)char.ToUpperInvariant(c),
            [var d] when char.IsAsciiDigit(d) => VirtualKey.Number0 + (d - '0'),
            "enter" => VirtualKey.Enter, "esc" or "escape" => VirtualKey.Escape, "tab" => VirtualKey.Tab, "up" => VirtualKey.Up, "down" => VirtualKey.Down,
            "left" => VirtualKey.Left, "right" => VirtualKey.Right, "pagedown" => VirtualKey.PageDown, "pageup" => VirtualKey.PageUp, "," => (VirtualKey)188,
            "[" => (VirtualKey)219, "]" => (VirtualKey)221, "\\" => (VirtualKey)220, ['f', _, ..] when int.TryParse(parts[^1][1..], out var n) => VirtualKey.F1 + (n - 1),
            _ => null
        };
        if (key is not { } k) return false;
        if (FieldText.FocusState != FocusState.Unfocused && FieldKey(k, modifiers.HasFlag(Alt))) return true;
        if (FindText.FocusState != FocusState.Unfocused && FindKey(k, modifiers.HasFlag(Shift))) return true;
        if (Switcher.Visibility == Visibility.Visible && modifiers == VirtualKeyModifiers.None && SwitcherKey(k)) return true;
        if (k == VirtualKey.Tab && modifiers.HasFlag(Ctrl)) { _browser.Step(modifiers.HasFlag(Shift) ? -1 : 1); return true; }
        if (!_keys.TryGetValue((k, modifiers), out var action)) return false;
        action();
        return true;
    }

    /// Esc puts away whatever is open: find, a panel, then full screen.
    private void PutAway()
    {
        if (_peek is not null) { ClosePeek(); return; }
        if (Switcher.Visibility == Visibility.Visible) { CloseSwitcher(); return; }
        if (_picking is not null) { StopPicking(); return; }
        if (FindBar.Visibility == Visibility.Visible) { CloseFind(); return; }
        if (_panel != PanelKind.None) { ClosePanel(); return; }
        if (_fullScreen) SetFullScreen(false);
    }

    private void CopyAddress()
    {
        if (_browser.Selected?.Url is not { } url) return;
        var package = new DataPackage();
        package.SetText(url.AbsoluteUri);
        Clipboard.SetContent(package);
    }

    private async Task PasteAndGo()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        Submit(await content.GetTextAsync(), newTab: false);
    }
}
