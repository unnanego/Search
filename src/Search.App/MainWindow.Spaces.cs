using System.ComponentModel;
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

    private void StartSpaces(WindowRecord? record, MainWindow? from)
    {
        _spaces = App.Shared.Spaces;
        _routes = App.Shared.Routes;
        // The space the window was last on, as the Mac picks it at launch; with spaces off, the home space. A new
        // window opens in the space of the window it came from.
        _space = from is not null && _spaces.Find(from._space) is not null ? from._space
            : record is not null || Profile == _spaces.Start().Profile ? _spaces.Start(record?.Space).Space
            : _spaces.UsesSpaces ? _spaces.LastSpace(Profile) : _spaces.Home(Profile);
        _spaces.Current = _space;
        _browser.Store = _spaces.StoreName(_space);
        Hook<PropertyChangedEventHandler>(h => _spaces.PropertyChanged += h, h => _spaces.PropertyChanged -= h, (_, _) => DispatcherQueue.TryEnqueue(DrawSpaces));
        DrawSpaces();
    }

    private Space CurrentSpace => _spaces.Find(_space) ?? Space.First();

    /// To another space: this one's row set aside as it is, that one's brought back, or read from its session.
    private void GoToSpace(Guid id)
    {
        if (id == _space || _spaces.Find(id) is null || !_spaces.UsesSpaces) return;
        SaveSession(now: true);
        // The row taken down and another put up: neither is a change to any space's pins.
        _following = true;
        try { Swap(id); } finally { _following = false; }
        // Its pins as they are now: another window may have changed them while it was set aside.
        FollowPins();
        DrawSpaces();
        SaveSession(now: true);
    }

    private void Swap(Guid id)
    {
        ClosePeek();
        CloseFind();
        var row = _browser.Ordinary.ToList();
        _aside[_space] = (row, _browser.Selected, _browser.Splits.ToList(), _browser.Groups.ToList());
        foreach (var tab in row) _pages.Parked.Add(tab);
        _browser.Splits.Clear();
        _browser.Selected = null;
        foreach (var tab in row) _browser.Tabs.Remove(tab);
        _space = id;
        _spaces.Enter(id, primary: _owner);
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
        else RestoreRow(id);
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
        // Signed in with the others, it has no cookies of its own to erase.
        var detail = _spaces.OwnsStore(space) ? "Its tabs close, and its cookies and sign-ins are erased from this PC. History and bookmarks stay." : "Its tabs and pins close. Your sign-ins, history and bookmarks stay.";
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Remove {space.Name}?", Content = detail, PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) DeleteSpace(space.Id);
    }

    /// Any space of the profile, its home included, while the profile keeps another: every window showing it moves
    /// off it first (to the next one if it was the home), then it goes with its pins.
    private static void DeleteSpace(Guid id)
    {
        if (!App.Shared.Spaces.CanRemove(id)) return;
        foreach (var window in App.Windows.ToList()) window.LeaveSpace(id);
        if (App.Shared.Spaces.Remove(id)) App.Shared.Pins.Forget(id, App.Shared.Folders);
    }

    /// Off a space about to be deleted: to the next one in the order if it was the home, else to the home; its row set
    /// aside here closed.
    private void LeaveSpace(Guid id)
    {
        if (_retired) return;
        var profile = _spaces.ProfileOf(id);
        var home = _spaces.Home(profile);
        if (_space == id && _spaces.Of(profile).FirstOrDefault(s => s.Id != id) is { } next) GoToSpace(id == home ? next.Id : home);
        if (_aside.Remove(id, out var gone)) foreach (var tab in gone.Tabs) { _pages.Parked.Remove(tab); _pages.Sleep(tab); }
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
            if (_spaces.CanRemove(id))
            {
                var remove = new MenuFlyoutItem { Text = "Remove…" };
                remove.Click += (_, _) => _ = RemoveSpace(space);
                menu.Items.Add(remove);
            }
            else if (Profile != Search.Core.Profile.PersonalId && _spaces.Of(Profile).Count == 1)
            {
                // A profile's last space goes with the profile.
                var profile = App.Shared.Profiles.Get(Profile);
                var remove = new MenuFlyoutItem { Text = "Delete Profile…" };
                remove.Click += (_, _) => _ = RemoveProfile(profile.Id, $"“{space.Name}” is its last space. The profile's windows close, its spaces go with their tabs and pins, and its cookies, sign-ins, history and bookmarks are erased from this PC.");
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
