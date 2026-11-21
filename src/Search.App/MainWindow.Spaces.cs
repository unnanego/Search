using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Search.Core;

namespace Search.App;

// Spaces in the window (Spaces.swift, SpaceGradient.swift, SpaceRoutes.swift): separate sets of tabs in the one window,
// each with its own sign-ins if you like, a colour or a gradient washing the window, a row of space buttons at the foot
// of the column, Alt+1 to Alt+9 to go to one. Off unless turned on in Settings; until then there is the first space
// and nothing about it shows. A space you leave keeps its pages as they are, set aside, until you come back.
public sealed partial class MainWindow
{
    private Spaces _spaces = null!;
    private SpaceRoutes _routes = null!;
    private Guid _space = Session.FirstSpace;
    /// What each space left on screen: its tabs, the one in front and its splits, kept alive while another is shown.
    private readonly Dictionary<Guid, (List<Tab> Tabs, Tab? Selected, List<SplitPages> Splits, List<TabGroup> Groups)> _aside = new();

    private void StartSpaces()
    {
        _spaces = new Spaces(settings: App.Notes);
        _routes = new SpaceRoutes(_spaces);
        // The space the window was last on, as the Mac picks it at launch; with spaces off, the home space.
        _space = _spaces.Start().Space;
        _spaces.Current = _space;
        _browser.Store = _spaces.StoreName(_space);
        _spaces.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(DrawSpaces);
        DrawSpaces();
    }

    private Space CurrentSpace => _spaces.Find(_space) ?? Space.First();

    /// To another space: this one's row set aside as it is, that one's brought back, or read from its session.
    private void GoToSpace(Guid id)
    {
        if (id == _space || _spaces.Find(id) is null || !_spaces.UsesSpaces) return;
        SaveSession(now: true);
        ClosePeek();
        CloseFind();
        var row = _browser.Ordinary.ToList();
        _aside[_space] = (row, _browser.Selected, _browser.Splits.ToList(), _browser.Groups.ToList());
        foreach (var tab in row) _pages.Parked.Add(tab);
        _browser.Splits.Clear();
        _browser.Selected = null;
        foreach (var tab in row) _browser.Tabs.Remove(tab);
        _space = id;
        _spaces.Enter(id);
        _browser.Store = _spaces.StoreName(id);
        if (_aside.Remove(id, out var back))
        {
            _browser.Groups.Clear();
            _browser.Groups.AddRange(back.Groups);
            foreach (var tab in back.Tabs) { _pages.Parked.Remove(tab); _browser.Insert(tab, _browser.Ordinary.Count()); }
            _browser.Splits.AddRange(back.Splits);
            _row.Sync();
            if (back.Selected is { } front && _browser.Tabs.Contains(front)) _browser.Selected = front; else if (_browser.Ordinary.FirstOrDefault() is { } first) _browser.Selected = first; else NewTab();
        }
        else Restore(id);
        DrawSpaces();
        SaveSession(now: true);
    }

    private void GoToSpaceNumber(int n)
    {
        var mine = _spaces.Of(_spaces.ProfileOf(_space));
        if (_spaces.UsesSpaces && n >= 1 && n <= mine.Count) GoToSpace(mine[n - 1].Id);
    }

    private async Task AddSpace()
    {
        var name = new TextBox { PlaceholderText = "Work, Home, a project…" };
        var own = new CheckBox { Content = "Sign-ins of its own", IsChecked = false };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "New space", Content = new StackPanel { Spacing = 12, Children = { name, own } }, PrimaryButtonText = "Make", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _spaces.Add(name.Text, sharesSignIns: own.IsChecked != true) is not { } made) return;
        GoToSpace(made.Id);
    }

    private async Task RenameSpace(Space space)
    {
        var name = new TextBox { Text = space.Name };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Rename space", Content = name, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _spaces.Rename(space.Id, name.Text);
    }

    private async Task RemoveSpace(Space space)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Remove {space.Name}?", Content = "Its tabs close and its session is forgotten. Bookmarks and history stay.", PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (space.Id == _space) GoToSpace(_spaces.Home(_spaces.ProfileOf(space.Id)));
        if (_aside.Remove(space.Id, out var gone)) foreach (var tab in gone.Tabs) { _pages.Parked.Remove(tab); _pages.Sleep(tab); }
        _spaces.Remove(space.Id);
    }

    /// The row of space buttons, and the window washed in the space's colour or gradient.
    private void DrawSpaces()
    {
        SpacesBar.Children.Clear();
        SpacesBar.Visibility = _spaces.UsesSpaces ? Visibility.Visible : Visibility.Collapsed;
        Paint();
        if (!_spaces.UsesSpaces) return;
        foreach (var space in _spaces.Of(_spaces.ProfileOf(_space)))
        {
            var tint = space.Tint is { } rgb ? Brush(rgb) : (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"];
            var button = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(space.Id == _space ? 2 : 0),
                BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Background = tint,
                Content = new TextBlock { Text = space.Name.Length > 0 ? space.Name[..1].ToUpperInvariant() : "·", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) }
            };
            ToolTipService.SetToolTip(button, space.Name);
            var id = space.Id;
            button.Click += (_, _) => GoToSpace(id);
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += (_, _) => _ = RenameSpace(space);
            menu.Items.Add(rename);
            var colours = new MenuFlyoutSubItem { Text = "Colour" };
            for (var i = 0; i < Spaces.Colours.Count; i++)
            {
                var index = i;
                var item = new MenuFlyoutItem { Text = Spaces.ColourNames[i] };
                item.Click += (_, _) => _spaces.SetColour(id, index);
                colours.Items.Add(item);
            }
            menu.Items.Add(colours);
            if (!space.IsFirst)
            {
                var remove = new MenuFlyoutItem { Text = "Remove…" };
                remove.Click += (_, _) => _ = RemoveSpace(space);
                menu.Items.Add(remove);
            }
            button.ContextFlyout = menu;
            SpacesBar.Children.Add(button);
        }
        var add = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, Width = 28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(add, "New space");
        add.Click += (_, _) => _ = AddSpace();
        SpacesBar.Children.Add(add);
    }

    private static SolidColorBrush Brush(Rgb rgb, double alpha = 1) => new(Windows.UI.Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255)));

    /// The wash behind everything: the space's colour, faint, or its gradient.
    private Brush? Wash()
    {
        if (!_spaces.UsesSpaces || _spaces.WashOf(CurrentSpace, Root.ActualTheme == ElementTheme.Dark) is not { } wash) return null;
        if (wash.Stops.Count >= 2 && wash.Gradient is { } gradient)
        {
            var (start, end) = gradient.Points(0, 0, 1, 1);
            var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(start.X, start.Y), EndPoint = new Windows.Foundation.Point(end.X, end.Y), Opacity = wash.Opacity };
            foreach (var stop in wash.Stops) brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Brush(stop.Colour).Color, Offset = stop.Location });
            return brush;
        }
        return wash.Colour is { } colour ? Brush(colour, wash.Opacity) : null;
    }

    /// A link that a route sends to its space (Arc's Air Traffic Control): that space comes on screen first.
    private bool Route(Uri link)
    {
        if (_routes.SpaceFor(link) is not { } space || space.Id == _space) return false;
        GoToSpace(space.Id);
        return true;
    }
}
