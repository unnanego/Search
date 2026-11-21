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

    private void WireField()
    {
        FieldText.TextChanged += (_, _) => OnTyped();
        // A click into the field selects all of it, as the keys do (FocusField); done here only for the pointer, because
        // this arrives after the focus and would select what has been typed since.
        FieldText.GotFocus += (_, _) => { if (FieldFloats) FieldHost.Visibility = Visibility.Visible; if (FieldText.FocusState == FocusState.Pointer) DispatcherQueue.TryEnqueue(() => FieldText.SelectAll()); };
        FieldText.LostFocus += (_, _) => { if (Offers.FocusState != FocusState.Unfocused) return; HideOffers(); ShowAddress(); if (FieldFloats) FieldHost.Visibility = Visibility.Collapsed; };
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
        offers.AddRange(_browser.Ordinary
            .Where(t => t != _browser.Selected && t.Url is not null && (t.Shown.Contains(needle, StringComparison.OrdinalIgnoreCase) || t.Url.AbsoluteUri.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Take(OpenTabsOffered)
            .Select(t => new Suggestion(Search.Core.Address.Editable(t.Url!), t.Shown, t.Url!, SuggestionKind.Open) { Tab = t.Id }));
        var marked = BookmarkOffers(words).Where(b => offers.All(o => o.Url != b.Url)).ToList();
        offers.AddRange(marked);
        var visited = _history.Suggestions(words).Where(v => marked.All(b => History.Identity(b.Url) != History.Identity(v.Url))).ToList();
        offers.AddRange(visited);
        var search = new Suggestion(words, "Search " + SearchName, SearchFor(words), SuggestionKind.Search);
        // Words that can't be an address put the search first; an address puts it last.
        if (Search.Core.Address.UrlFrom(words) is null) offers.Insert(0, search); else offers.Add(search);
        Offers.ItemsSource = offers.Select(o => new Offer(o)).ToList();
        Offers.SelectedIndex = -1;
        Offers.Visibility = Visibility.Visible;
        if (!complete || _history.Completion(typed, visited) is not { Length: > 0 } rest) return;
        _completing = true;
        FieldText.Text = typed + rest;
        FieldText.Select(typed.Length, rest.Length);
        _known = FieldText.Text;
        _completing = false;
    }

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
            case VirtualKey.Down or VirtualKey.Up when Offers.Visibility == Visibility.Visible:
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
        Go(Search.Core.Address.UrlFrom(words) ?? Search.Core.Address.File(words) ?? SearchFor(words), newTab);
    }

    private void Take(Suggestion suggestion, bool newTab)
    {
        if (suggestion.Kind == SuggestionKind.Open && _browser.Ordinary.FirstOrDefault(t => t.Id == suggestion.Tab) is { } open) { HideOffers(); _browser.Selected = open; _ = Front(open, givePage: true); return; }
        Go(suggestion.Url, newTab);
    }

    private void Go(Uri url, bool newTab)
    {
        HideOffers();
        var tab = _browser.Selected;
        if (newTab || tab is null) tab = _browser.Open(url, @private: tab?.Private == true && !newTab); else tab.Go(url);
        if (FieldFloats) FieldHost.Visibility = Visibility.Collapsed;
        _ = Front(tab, givePage: true);
    }
}
