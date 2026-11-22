using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Windows.Storage.Pickers;

namespace Search.App;

// Profiles (Profiles.swift) and Sync (Sync.swift) as a window shows them. A profile is a whole separate browser in the
// app: its own spaces, tabs, pins, sign-ins, history and bookmarks; a window is one profile's for its whole life, and
// says which in its title once there is more than one. Until a second profile is made nothing about profiles shows.
// Settings makes, renames and removes them, and opens a window of one; it also turns Sync on with a folder.
public sealed partial class MainWindow
{
    /// The window's title: the tab in front's, and the profile's name once there are several.
    private string Titled(Tab? tab)
    {
        var title = tab is null ? "Search" : Shown(tab);
        return App.Shared.Profiles.Several ? $"{title} — {App.Shared.Profiles.Get(Profile).Name}" : title;
    }

    /// A space another computer removed (Sync): off screen here, its tabs closed.
    public void SpaceGone(Guid space)
    {
        if (_retired) return;
        if (_space == space) GoToSpace(_spaces.Home(Profile));
        if (_aside.Remove(space, out var gone)) foreach (var tab in gone.Tabs) { _pages.Parked.Remove(tab); _pages.Sleep(tab); }
    }

    /// Settings › Profiles: each profile, a window of it, its name, and taking it away; and a new one.
    private StackPanel ProfileSettings()
    {
        var profiles = App.Shared.Profiles;
        var list = new StackPanel { Spacing = 6 };
        list.Children.Add(new TextBlock { Text = "Profiles", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        list.Children.Add(new TextBlock { Text = "Each profile is a browser of its own: its spaces, tabs, sign-ins, history and bookmarks. Settings, extensions and passwords are shared.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        foreach (var profile in profiles.List)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = profile.Name + (profile.Id == Profile ? " (this window)" : ""), VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 });
            var open = new Button { Content = "Open a window" };
            var id = profile.Id;
            open.Click += (_, _) => { _settings?.Hide(); App.WindowOf(id).Bring(); };
            row.Children.Add(open);
            var rename = new Button { Content = "Rename…" };
            rename.Click += (_, _) => { _settings?.Hide(); _ = RenameProfile(id); };
            row.Children.Add(rename);
            if (!profile.IsPersonal)
            {
                var remove = new Button { Content = "Remove…" };
                remove.Click += (_, _) => { _settings?.Hide(); _ = RemoveProfile(id); };
                row.Children.Add(remove);
            }
            list.Children.Add(row);
        }
        var add = new Button { Content = "New profile…" };
        add.Click += (_, _) => { _settings?.Hide(); _ = AddProfile(); };
        list.Children.Add(add);
        return list;
    }

    private async Task<string?> AskName(string title, string text, string action)
    {
        var name = new TextBox { Text = text, PlaceholderText = "Work, School, Personal…" };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = name, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && name.Text.Trim() is { Length: > 0 } named ? named : null;
    }

    private async Task AddProfile() { if (await AskName("New profile", "", "Make") is { } name) MakeProfile(name); }

    /// A profile made, and a window of it opened.
    private MainWindow? MakeProfile(string name)
    {
        if (App.Shared.Profiles.Add(name, _spaces) is not { } made) return null;
        RetitleAll();
        var window = App.Open(profile: made.Id);
        window.Bring();
        return window;
    }

    private async Task RenameProfile(Guid id) { if (await AskName("Rename profile", App.Shared.Profiles.Get(id).Name, "Rename") is { } name && App.Shared.Profiles.Rename(id, name)) RetitleAll(); }

    private async Task RemoveProfile(Guid id)
    {
        var profile = App.Shared.Profiles.Get(id);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"Remove {profile.Name}?", Content = "Its windows close, and its spaces, tabs, history, bookmarks and sign-ins are deleted from this PC.", PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) DeleteProfile(id);
    }

    /// Its windows closed first (a Personal one opened if none would be left), then the profile and all it held.
    private static void DeleteProfile(Guid id)
    {
        if (id == Search.Core.Profile.PersonalId || !App.Shared.Profiles.Has(id)) return;
        if (App.Windows.All(w => w.Profile == id)) App.Open(profile: Search.Core.Profile.PersonalId);
        foreach (var window in App.Windows.Where(w => w.Profile == id).ToList()) window.Discard();
        App.Shared.HistoryOf(id).Retire();
        App.Shared.Profiles.Delete(id, App.Shared.Spaces, App.Shared.Pins);
        RetitleAll();
    }

    private static void RetitleAll() { foreach (var window in App.Windows) window.Title = window.Titled(window._browser.Selected); }

    /// Settings › Sync: on with a folder, off, and when it last ran.
    private StackPanel SyncSettings()
    {
        var sync = App.Shared.Sync;
        var box = new StackPanel { Spacing = 6 };
        var on = new ToggleSwitch { Header = "Sync with your other computers", IsOn = sync.On };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        void Say() => note.Text = sync.On ? sync.Summary(Ago) : "Spaces, pins, bookmarks and some settings, through a folder you choose (OneDrive, Dropbox, a shared drive) that your computers share, Macs included. Never history, passwords, sign-ins or open tabs.";
        Say();
        var now = new Button { Content = "Sync now", Visibility = sync.On ? Visibility.Visible : Visibility.Collapsed };
        now.Click += async (_, _) => { await App.Syncing.Now(); Say(); };
        on.Toggled += async (_, _) =>
        {
            if (on.IsOn == sync.On) return;
            if (on.IsOn)
            {
                if (await PickFolder() is { } folder) App.Syncing.TurnOn(folder); else on.IsOn = false;
            }
            else App.Syncing.TurnOff();
            now.Visibility = sync.On ? Visibility.Visible : Visibility.Collapsed;
            Say();
        };
        box.Children.Add(on);
        box.Children.Add(note);
        box.Children.Add(now);
        return box;
    }

    private static string Ago(DateTimeOffset when)
    {
        var gone = DateTimeOffset.UtcNow - when;
        return gone.TotalMinutes < 1 ? "just now" : gone.TotalHours < 1 ? $"{(int)gone.TotalMinutes} min ago" : gone.TotalDays < 1 ? $"{(int)gone.TotalHours} h ago" : when.ToLocalTime().ToString("d MMM");
    }

    private async Task<string?> PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
