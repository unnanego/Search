using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Search.Core;
using Search.Engine;

namespace Search.App;

// Split View in the window (SplitView.swift, PaneStage.swift): the dividers between the pages, dragged to share the
// room out, and the outline of the page that has the keys. Ctrl+\ splits the current page, Ctrl+Alt+← and → go from
// one page to the other, Ctrl+Shift+\ separates them, and Alt+click on a tab puts it beside the page in front.
public sealed partial class MainWindow
{
    private void WireSplit()
    {
        _pages.Arranged += DrawSplit;
        _pages.Focused += tab => { if (tab != _browser.Selected && _browser.SplitOf(_browser.Selected)?.Tabs.Contains(tab) == true) _browser.Selected = tab; };
        Row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRowPointer), true);
        Strip.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRowPointer), true);
    }

    /// Alt+click on a tab: beside the page in front, in a split.
    private void OnRowPointer(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu) || e.OriginalSource is not FrameworkElement { DataContext: Tab tab } || _browser.Selected is not { } front || tab == front) return;
        _browser.Split(front, tab);
        e.Handled = true;
    }

    /// Ctrl+\: a new page beside this one, waiting for an address.
    private void SplitCurrent()
    {
        if (_browser.Selected is not { } front || _browser.SplitOf(front) is { Tabs.Count: >= SplitPages.Most }) return;
        var page = _browser.Open(null, select: false, after: front, @private: front.Private);
        _browser.Split(front, page);
        FocusField();
    }

    private void SeparateCurrent() { if (_browser.SplitOf(_browser.Selected) is { } split) _browser.Separate(split); }

    private void DrawSplit()
    {
        SplitMarks.Children.Clear();
        var split = _browser.SplitOf(_browser.Selected);
        if (split is null) return;
        var panes = _pages.Panes;
        var horizontal = split.Axis == SplitAxis.Horizontal;
        for (var i = 0; i < panes.Count - 1; i++)
        {
            var rect = panes[i].Rect;
            var divider = new Divider(horizontal) { Width = horizontal ? Pages.Gap : rect.Width, Height = horizontal ? rect.Height : Pages.Gap };
            Canvas.SetLeft(divider, horizontal ? rect.Right : 0);
            Canvas.SetTop(divider, horizontal ? 0 : rect.Bottom);
            var index = i;
            divider.ManipulationDelta += (_, e) =>
            {
                var total = horizontal ? Stage.ActualWidth : Stage.ActualHeight;
                var edge = (horizontal ? Canvas.GetLeft(divider) : Canvas.GetTop(divider)) + (horizontal ? e.Delta.Translation.X : e.Delta.Translation.Y);
                if (total <= 0) return;
                split.Drag(index, (edge - Pages.Gap * index) / (total - Pages.Gap * (panes.Count - 1)));
                _browser.Resized(split);
            };
            SplitMarks.Children.Add(divider);
        }
        // The page with the keys, outlined: page commands go to it.
        if (panes.FirstOrDefault(p => p.Tab == _browser.Selected) is { Tab: not null } focused)
        {
            var outline = new Border { Width = focused.Rect.Width, Height = focused.Rect.Height, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), IsHitTestVisible = false,
                BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] };
            Canvas.SetLeft(outline, focused.Rect.X);
            Canvas.SetTop(outline, focused.Rect.Y);
            SplitMarks.Children.Add(outline);
        }
    }

    /// The bar between two pages: dragged, it shares the room out between them.
    private sealed partial class Divider : Grid
    {
        public Divider(bool horizontal)
        {
            Background = new SolidColorBrush(Colors.Transparent);
            ManipulationMode = horizontal ? ManipulationModes.TranslateX : ManipulationModes.TranslateY;
            ProtectedCursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            Children.Add(new Border { Width = horizontal ? 2 : double.NaN, Height = horizontal ? double.NaN : 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                VerticalAlignment = horizontal ? VerticalAlignment.Stretch : VerticalAlignment.Center, Margin = horizontal ? new Thickness(0, 40, 0, 40) : new Thickness(40, 0, 40, 0),
                Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] });
        }
    }
}
