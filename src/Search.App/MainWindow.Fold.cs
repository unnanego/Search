using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Search.App;

// The column of tabs, folded away with Ctrl+S (Fold.swift). Folded, the page takes the whole window; the tabs are one
// push against its left edge away: the column slides out over the page, the same column with the same rows, and goes
// again once the pointer leaves it, after a short grace so a hand that overshoots doesn't lose it.
public sealed partial class MainWindow
{
    private bool _slidOut;
    private int _leaving;

    private void WireFold()
    {
        Edge.PointerEntered += (_, _) => SlideOut();
        Column.PointerEntered += (_, _) => _leaving++;
        Column.PointerExited += (_, _) => _ = SlideInSoon();
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Search.Core.Browser.Selected) && _slidOut) SlideIn(); };
    }

    private bool Folded => _prefs.TabsOnSide && _prefs.Folded && !_fullScreen;

    /// Called by Lay: the edge is there only while the column is folded.
    private void LayFold()
    {
        Edge.Visibility = Folded ? Visibility.Visible : Visibility.Collapsed;
        if (!Folded && _slidOut) SlideIn(relay: false);
    }

    private void SlideOut()
    {
        if (!Folded || _slidOut) return;
        _slidOut = true;
        Grid.SetColumn(Column, 0);
        Grid.SetColumnSpan(Column, 2);
        Column.HorizontalAlignment = HorizontalAlignment.Left;
        Column.Width = _prefs.ColumnWidth;
        Column.Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"];
        Canvas.SetZIndex(Column, 10);
        Column.Visibility = Visibility.Visible;
        // The field comes with the column, as it is when the column is out.
        Detach(Field);
        ColumnFieldSlot.Child = Field;
    }

    private async Task SlideInSoon()
    {
        var mine = ++_leaving;
        await Task.Delay(400);
        if (mine == _leaving && _slidOut && FieldText.FocusState == FocusState.Unfocused) SlideIn();
    }

    private void SlideIn(bool relay = true)
    {
        _slidOut = false;
        Grid.SetColumnSpan(Column, 1);
        Column.HorizontalAlignment = HorizontalAlignment.Stretch;
        Column.Width = double.NaN;
        Column.Background = null;
        Canvas.SetZIndex(Column, 0);
        if (relay) Lay();
    }
}
