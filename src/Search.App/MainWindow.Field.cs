using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Search.Core;
using Windows.System;

namespace Search.App;

// The one field (Omnibox.swift): an address goes there, words are searched. It finishes addresses from your own
// history as you type, offers open tabs, places you've been and a search, and sends nothing anywhere until Enter.
public sealed partial class MainWindow
{
    private const int OpenTabsOffered = 3;
    private string _typed = "";
    private bool _completing;
    /// The text the field last held that we know of, typed or set here. TextChanged arrives after the change and
    /// after any flag around it is down, so a change we made is told apart by its text.
    private string _known = "";
    /// The field was raised by Ctrl+T, for a new tab: with Ctrl+T finds your open tabs on, what is typed looks for them.
    private bool _launching;
    /// The first row was picked by what was typed, not by the arrow keys: Enter takes it, ↑ lets go of it.
    private bool _pickedByTyping;

    private void WireField()
    {
        FieldText.TextChanged += (_, _) => OnTyped();
        // A click into the field selects all of it, as the keys do (FocusField); done here only for the pointer, because
        // this arrives after the focus and would select what has been typed since.
        FieldText.GotFocus += (_, _) => { if (FieldFloats) FieldHost.Visibility = Visibility.Visible; if (FieldText.FocusState == FocusState.Pointer) DispatcherQueue.TryEnqueue(() => FieldText.SelectAll()); };
        FieldText.LostFocus += (_, _) => { if (Offers.FocusState != FocusState.Unfocused) return; _launching = false; HideOffers(); ShowAddress(); if (FieldFloats) FieldHost.Visibility = Visibility.Collapsed; };
        FieldText.PreviewKeyDown += OnFieldKey;
        Offers.ItemClick += (_, e) => { if (e.ClickedItem is Offer offer) Take(offer.Suggestion, newTab: false); };
    }

    public void FocusField()
    {
        _keysAsked++;
        if (FieldFloats) FieldHost.Visibility = Visibility.Visible;
        FieldText.Focus(FocusState.Keyboard);
        FieldText.SelectAll();
    }

    /// The field at rest says where the tab in front is, as an address you could send again and land on the same page.
    private void ShowAddress()
    {
        if (FieldText.FocusState != FocusState.Unfocused) return;
        _completing = true;
        FieldText.Text = _browser.Selected?.Url is { } url ? Search.Core.Address.Editable(url) : "";
        _completing = false;
        _typed = _known = FieldText.Text;
    }

    private void OnTyped()
    {
        if (_completing || FieldText.FocusState == FocusState.Unfocused || FieldText.Text == _known) return;
        var text = FieldText.Text;
        _known = text;
        // Finished only when a letter was added at the end; deleting must be able to take a completion away.
        var forward = text.Length > _typed.Length && text.StartsWith(_typed, StringComparison.Ordinal) && FieldText.SelectionStart == text.Length;
        _typed = text;
        Offer(text, forward);
    }

    private void Offer(string typed, bool complete)
    {
        var words = typed.Trim();
        if (words.Length == 0) { HideOffers(); return; }
        var offers = new List<Suggestion>();
        var needle = words.ToLowerInvariant();
        _pickedByTyping = false;
        var finding = _launching && _prefs.FindsOpenTabs;
        if (finding) offers.AddRange(TabOffers(typed));
        else offers.AddRange(_browser.Ordinary
            .Where(t => t != _browser.Selected && t.Url is not null && (t.Shown.Contains(needle, StringComparison.OrdinalIgnoreCase) || t.Url.AbsoluteUri.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Take(OpenTabsOffered)
            .Select(t => new Suggestion(Search.Core.Address.Editable(t.Url!), t.Shown, t.Url!, SuggestionKind.Open) { Tab = t.Id }));
        var (places, ending) = PlaceOffers(typed, offers);
        var found = finding && offers.Count > 0;
        if (found) places.RemoveAll(p => p.Kind != SuggestionKind.Search && offers.Any(o => History.Identity(o.Url) == History.Identity(p.Url)));
        // Words that can't be an address put the search first, over the open tabs too; tabs found for Ctrl+T stay on top.
        else if (places.FirstOrDefault() is { Kind: SuggestionKind.Search } first) { offers.Insert(0, first); places.RemoveAt(0); }
        offers.AddRange(places);
        Offers.ItemsSource = offers.Select(o => new Offer(o)).ToList();
        Offers.SelectedIndex = found ? 0 : -1;
        Offers.Visibility = Visibility.Visible;
        // The first tab found is picked as you type, and only its address is finished in grey, never history's.
        if (found) { _pickedByTyping = true; ending = TabSearch.Ending(typed, Search.Core.Address.Pretty(offers[0].Url)); }
        if (!complete || ending is not { } rest) return;
        _completing = true;
        FieldText.Text = typed + rest;
        FieldText.Select(typed.Length, rest.Length);
        _known = FieldText.Text;
        _completing = false;
    }

    /// The field's places for words: bookmarks (less any page in `open`), where you've been, and a search, first for
    /// words that can't be an address and last for one; and the rest of the best match, to finish what was typed.
    internal (List<Suggestion> Places, string? Ending) PlaceOffers(string typed, IReadOnlyList<Suggestion>? open = null)
    {
        var words = typed.Trim();
        var marked = BookmarkOffers(words).Where(b => open is null || open.All(o => o.Url != b.Url)).ToList();
        var visited = _history.Suggestions(words).Where(v => marked.All(b => History.Identity(b.Url) != History.Identity(v.Url))).ToList();
        var places = marked.Concat(visited).ToList();
        var search = new Suggestion(words, "Search " + SearchName, SearchFor(words), SuggestionKind.Search);
        if (Search.Core.Address.UrlFrom(words) is null) places.Insert(0, search); else places.Add(search);
        return (places, _history.Completion(typed, visited) is { Length: > 0 } rest ? rest : null);
    }

    /// Where Enter on typed words goes: a place if it is one (a file on this machine counts), else a search.
    internal Uri Destination(string typed) { var words = typed.Trim(); return Search.Core.Address.UrlFrom(words) ?? Search.Core.Address.File(words) ?? SearchFor(words); }

    /// Ctrl+T's offers of what you have open: tabs, then pins, that answer to what is typed (TabSearch), not the tab
    /// in front, nor a blank one, a private one's page or one a script opened.
    private IEnumerable<Suggestion> TabOffers(string typed) =>
        TabSearch.Ranked(_browser.Ordinary.Where(t => t != _browser.Selected && t.Url is not null && !t.Agent), typed, t => t.Shown, t => Search.Core.Address.Pretty(t.Url!), t => t.Pinned, t => t.Touched)
            .Select(t => new Suggestion(Search.Core.Address.Editable(t.Url!), t.Shown, t.Url!, SuggestionKind.Open) { Tab = t.Id });

    private void HideOffers() { Offers.Visibility = Visibility.Collapsed; Offers.ItemsSource = null; }

    private void OnFieldKey(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (FieldKey(e.Key, alt)) e.Handled = true;
    }

    /// The field's own keys: ↓ ↑ through the list, Enter to go (Alt+Enter in a new tab), Esc back to the page.
    private bool FieldKey(VirtualKey key, bool alt)
    {
        switch (key)
        {
            // A tab picked by what was typed, let go of: its grey ending goes too, so Enter opens what was typed.
            case VirtualKey.Up when _pickedByTyping && Offers.SelectedIndex == 0:
                _pickedByTyping = false;
                Offers.SelectedIndex = -1;
                if (FieldText.SelectionLength > 0 && FieldText.SelectionStart + FieldText.SelectionLength == FieldText.Text.Length) { _completing = true; FieldText.Text = FieldText.Text[..FieldText.SelectionStart]; FieldText.SelectionStart = FieldText.Text.Length; _known = FieldText.Text; _completing = false; }
                return true;
            case VirtualKey.Down or VirtualKey.Up when Offers.Visibility == Visibility.Visible:
                _pickedByTyping = false;
                var count = Offers.Items.Count;
                Offers.SelectedIndex = count == 0 ? -1 : ((Offers.SelectedIndex + (key == VirtualKey.Down ? 1 : -1)) % count + count) % count;
                return true;
            case VirtualKey.Enter:
                if (Offers.SelectedItem is Offer chosen) Take(chosen.Suggestion, alt); else Submit(FieldText.Text, alt);
                return true;
            case VirtualKey.Escape:
                HideOffers();
                ShowAddressNow();
                if (_browser.Selected is { Url: not null } tab) _pages.ViewOf(tab)?.Focus(FocusState.Programmatic);
                return true;
            default:
                return false;
        }
    }

    /// Typed as a person would, a letter at a time, into whichever box has the keys (the bench's test-only `type`).
    internal void TypeIntoNamed(string into, string text) => TypeInto(into switch { "find" => FindText, "panel" => PanelSearch, "switcher" => SwitcherText, _ => FieldText }, text);

    private void TypeInto(TextBox box, string text)
    {
        if (box.FocusState == FocusState.Unfocused) box.Focus(FocusState.Programmatic);
        foreach (var letter in text)
        {
            var at = box.SelectionStart;
            box.Text = box.Text.Remove(at, box.SelectionLength).Insert(at, letter.ToString());
            box.SelectionStart = at + 1;
            box.SelectionLength = 0;
            if (box == FieldText) OnTyped();
        }
    }

    private void ShowAddressNow() { HideOffers(); _completing = true; FieldText.Text = _browser.Selected?.Url is { } url ? Search.Core.Address.Editable(url) : ""; _completing = false; _typed = _known = FieldText.Text; FieldText.SelectAll(); }

    /// Enter on what was typed: a place if it is one (a file on this machine counts), else a search.
    private void Submit(string typed, bool newTab)
    {
        var words = typed.Trim();
        if (words.Length == 0) return;
        Go(Destination(words), newTab);
    }

    private void Take(Suggestion suggestion, bool newTab)
    {
        if (suggestion.Kind == SuggestionKind.Open && _browser.Ordinary.FirstOrDefault(t => t.Id == suggestion.Tab) is { } open)
        {
            // From Ctrl+T's blank tab, that tab isn't wanted any more: the page you had is where you go.
            var blank = _launching && _browser.Selected is { Url: null, Pinned: false } empty && empty != open ? empty : null;
            _launching = false;
            HideOffers();
            _browser.Selected = open;
            if (blank is not null) _browser.Close(blank);
            _ = Front(open, givePage: true);
            return;
        }
        Go(suggestion.Url, newTab);
    }

    private void Go(Uri url, bool newTab)
    {
        _launching = false;
        HideOffers();
        var tab = _browser.Selected;
        if (newTab || tab is null) tab = _browser.Open(url, @private: tab?.Private == true && !newTab); else tab.Go(url);
        if (FieldFloats) FieldHost.Visibility = Visibility.Collapsed;
        _ = Front(tab, givePage: true);
    }
}
