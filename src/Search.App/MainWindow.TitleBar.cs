using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Search.App;

// The title bar (TabBar.swift's strip). All of the top bar moves the window and a double-click on it maximizes, as a
// title bar's does; only what is clickable in it, the tabs across the top and the buttons beside them, is cut out. A
// band above the tabs stays title bar, so a full row can still be taken hold of (#1321). The row of tabs is never wider
// than the bar leaves it, so its last tab is never cut off: past that it scrolls, and the tab in front is brought into
// sight (#1295).
public sealed partial class MainWindow
{
    /// What the strip leaves of the bar at least, beside the caption buttons, to take hold of the window by.
    private const double DragRoom = 40;
    /// The caption buttons' column, the bar's last (MainWindow.xaml).
    private const double CaptionRoom = 140;
    /// How much of the strip's top stays title bar. The system takes the window's top 4 pixels or so for resizing, and
    /// the tabs are drawn from 6 down, so this leaves a band to take hold of above them, as on the Mac.
    private const double AboveTabs = 6;

    /// What is cut out of the title bar now, in the window's pixels.
    private RectInt32[] _passthrough = [];

    private void WireTitleBar()
    {
        TopBar.SizeChanged += (_, _) => FitStrip();
        TopExtensionsSlot.SizeChanged += (_, _) => FitStrip();
        Strip.SizeChanged += (_, _) => { CutTitleBar(); RevealSelected(); };
        Strip.Loaded += (_, _) => { if (Descendant<ScrollViewer>(Strip) is { } scroller) scroller.ViewChanged += (_, _) => CutTitleBar(); };
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => CutTitleBar();
    }

    /// Called by Lay: the whole bar is the title bar, down the left or across the top.
    private void LayTitleBar()
    {
        SetTitleBar(TopBar);
        FitStrip();
    }

    /// The strip as wide as its tabs, up to what the bar leaves it beside the extensions, the drag room and the caption
    /// buttons. Unbounded it took all its tabs' width and pushed the rest out of the window.
    private void FitStrip()
    {
        var beside = TopExtensionsSlot.ActualWidth + TopExtensionsSlot.Margin.Left + TopExtensionsSlot.Margin.Right;
        Strip.MaxWidth = Math.Max(0, TopBar.ActualWidth - CaptionRoom - DragRoom - beside - Strip.Margin.Left - Strip.Margin.Right);
        CutTitleBar();
    }

    /// The tabs and the buttons beside them take their own clicks; the rest of the bar is the title bar's.
    private void CutTitleBar()
    {
        if (Root.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        RectInt32? Cut(FrameworkElement element, double top = 0)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= top) return null;
            var box = element.TransformToVisual(null).TransformBounds(new Rect(0, top, element.ActualWidth, element.ActualHeight - top));
            return new RectInt32((int)Math.Round(box.X * scale), (int)Math.Round(box.Y * scale), (int)Math.Round(box.Width * scale), (int)Math.Round(box.Height * scale));
        }
        var cuts = new[] { Cut(Strip, AboveTabs), Cut(TopExtensionsSlot) }.OfType<RectInt32>().ToArray();
        if (cuts.SequenceEqual(_passthrough)) return;
        _passthrough = cuts;
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, cuts);
    }

    /// The tab in front in sight in the strip, after a new tab at its end or a window made narrower.
    private void RevealSelected() { if (Strip.Visibility == Visibility.Visible && _browser.Selected is { } tab && _row.Flat.Contains(tab)) Strip.ScrollIntoView(tab); }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (VisualTreeHelper.GetChild(parent, i) is var child && (child as T ?? Descendant<T>(child)) is { } found) return found;
        return null;
    }
}
