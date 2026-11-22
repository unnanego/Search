using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Windows.Storage.Pickers;

namespace Search.App;

// Bringing things over from another browser, all in one place (ImportPanel.swift): which browser, which of its
// profiles, and what of it — bookmarks, history, passwords, the tabs it has open, Arc's or Zen's spaces and pins, and
// its extensions from the Chrome Web Store. What each holds is counted as the sheet opens, from its files alone, off
// the window's thread; nothing is decrypted until "Bring them in". Chrome since 127 keeps passwords no other app can
// read (app-bound): those are counted, and the sheet says how to export them to a CSV file, which it also takes.
public sealed partial class MainWindow
{
    private ImportRecords Records => _records ??= new ImportRecords(App.Notes);
    private ImportRecords? _records;

    /// What can be brought from a browser, as ticked.
    [Flags]
    public enum Bringing { None = 0, Bookmarks = 1, History = 2, Passwords = 4, Tabs = 8, Sidebar = 16, Extensions = 32, All = 63 }

    private async Task OpenImport()
    {
        var sources = await Task.Run(() => ImportSource.Installed());
        var csv = new Button { Content = "Passwords from a file (.csv)…" };
        csv.Click += async (_, _) => { if (await PickCsv() is { } file) Notice(TakePasswordFile(file)); };
        if (sources.Count == 0)
        {
            var none = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Bring in from another browser", Content = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "No other browser's data was found on this PC. A passwords file another browser exported can still come in.", TextWrapping = TextWrapping.Wrap }, csv } }, CloseButtonText = "Close", RequestedTheme = Root.ActualTheme };
            await none.ShowAsync();
            return;
        }
        var browser = new ComboBox { Header = "Browser", ItemsSource = sources.Select(s => s.Name).ToList(), SelectedIndex = 0, MinWidth = 260 };
        var profile = new ComboBox { Header = "Profile", MinWidth = 260 };
        var boxes = new Dictionary<Bringing, CheckBox>();
        foreach (var kind in new[] { Bringing.Bookmarks, Bringing.History, Bringing.Passwords, Bringing.Tabs, Bringing.Sidebar, Bringing.Extensions }) boxes[kind] = new CheckBox { IsChecked = kind is Bringing.Bookmarks or Bringing.History or Bringing.Passwords };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var lastNote = Records;
        var panel = new StackPanel { Spacing = 10, MinWidth = 420, Children = { browser, profile } };
        foreach (var box in boxes.Values) panel.Children.Add(box);
        panel.Children.Add(note);
        panel.Children.Add(csv);
        ImportSource Source() => sources[Math.Max(0, browser.SelectedIndex)];
        string? Chosen() => profile.SelectedIndex >= 0 && profile.SelectedIndex < Source().Profiles.Count ? Source().Profiles[profile.SelectedIndex].Id : Source().Usual;
        async Task Count()
        {
            var source = Source();
            var chosen = Chosen();
            foreach (var box in boxes.Values) { box.Content = "Counting…"; box.IsEnabled = false; }
            var (preview, sidebar) = await Task.Run(() => (source.Preview(chosen), source.SidebarCounts(chosen)));
            if (Source() != source) return;
            void Say(Bringing kind, string text, int count) { boxes[kind].Content = text; boxes[kind].IsEnabled = count > 0; if (count == 0) boxes[kind].IsChecked = false; }
            Say(Bringing.Bookmarks, $"Bookmarks ({preview.Bookmarks})", preview.Bookmarks);
            Say(Bringing.History, $"History ({preview.Places} places)", preview.Places);
            Say(Bringing.Passwords, preview.AppBound > 0 ? $"Passwords ({preview.Passwords}; {preview.AppBound} more {source.Name} keeps to itself)" : $"Passwords ({preview.Passwords})", preview.Passwords);
            Say(Bringing.Tabs, $"Its open tabs ({preview.Session.Tabs})", preview.Session.Tabs);
            Say(Bringing.Sidebar, sidebar is { } s ? $"Its spaces and pins ({s.Spaces} spaces, {s.Pinned} pinned)" : "Spaces and pins", sidebar is { Spaces: > 0 } ? 1 : 0);
            boxes[Bringing.Sidebar].Visibility = sidebar is null ? Visibility.Collapsed : Visibility.Visible;
            Say(Bringing.Extensions, $"Its extensions from the Chrome Web Store ({preview.Extensions.Count})", preview.Extensions.Count);
            var record = Records.Of(source.Name);
            note.Text = string.Join(" ", new[]
            {
                record is null ? null : $"Brought in {record.Date.ToLocalTime():d MMM}: what's already here isn't added twice.",
                preview.AppBound > 0 ? (source as ChromiumSource)?.ExportSteps ?? "Export its passwords to a CSV file and bring that in." : null
            }.OfType<string>());
        }
        browser.SelectionChanged += (_, _) =>
        {
            profile.ItemsSource = Source().Profiles.Select(p => p.Name).ToList();
            profile.SelectedIndex = Math.Max(0, Source().Profiles.ToList().FindIndex(p => p.Id == Source().Usual));
            profile.Visibility = Source().Profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _ = Count();
        };
        profile.SelectionChanged += (_, _) => _ = Count();
        profile.ItemsSource = Source().Profiles.Select(p => p.Name).ToList();
        profile.SelectedIndex = Math.Max(0, Source().Profiles.ToList().FindIndex(p => p.Id == Source().Usual));
        profile.Visibility = Source().Profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _ = Count();
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Bring in from another browser", Content = new ScrollViewer { Content = panel }, PrimaryButtonText = "Bring them in", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var what = boxes.Where(b => b.Value.IsChecked == true && b.Value.IsEnabled).Aggregate(Bringing.None, (all, b) => all | b.Key);
        Notice(await BringIn(Source(), Chosen(), what));
    }

    /// What was ticked, taken in, and what the window says of it.
    private async Task<string> BringIn(ImportSource source, string? profile, Bringing what)
    {
        var said = new List<string>();
        var name = source.Name;
        if (what.HasFlag(Bringing.Bookmarks)) said.Add(ImportTake.Bookmarks(_bookmarks, Records, name, await Task.Run(() => source.Bookmarks(profile))).Said(name));
        if (what.HasFlag(Bringing.History)) said.Add($"{ImportTake.Places(_history, await Task.Run(() => source.Places(profile)), Records, name)} places from its history");
        if (what.HasFlag(Bringing.Passwords))
        {
            var found = await Task.Run(() => source.Passwords(profile));
            var (kept, skipped) = _vault.Take(found.Logins.Select(l => l.ToImported()));
            foreach (var host in found.Never) _vault.NeverFor(host);
            Records.Note(name, passwords: kept);
            said.Add(found.Trouble != LoginTrouble.None && found.Logins.Count == 0 ? found.Advice ?? "Its passwords couldn't be read" : $"{kept} passwords" + (skipped > 0 ? $", {skipped} already here" : ""));
            if (found.AppBound > 0) said.Add($"{found.AppBound} more {name} keeps to itself: {found.Advice ?? "export them to a CSV file and bring that in"}");
        }
        var target = new ImportTarget(_spaces, Pinned, App.Shared.Folders, Profile, _space) { Asleep = Asleep, From = null };
        if (what.HasFlag(Bringing.Tabs))
        {
            var taken = ImportTake.OpenTabs(await Task.Run(() => source.Sessions(profile)), target);
            _row.Sync();
            said.Add($"{taken.Tabs} tabs" + (taken.Groups > 0 ? $" in {taken.Groups} groups" : "") + (taken.Pins > 0 ? $", {taken.Pins} pins" : ""));
        }
        if (what.HasFlag(Bringing.Sidebar) && await Task.Run(() => source.Sidebar(profile)) is { } sidebar)
        {
            var taken = ImportTake.Sidebar(sidebar, target);
            Records.Note(name, spaces: taken.Spaces, pinned: taken.Pins);
            _row.Sync();
            DrawSpaces();
            said.Add($"{taken.Spaces} spaces, {taken.Pins} pins");
        }
        if (what.HasFlag(Bringing.Extensions))
        {
            var ids = await Task.Run(() => source.Extensions(profile));
            var added = 0;
            foreach (var id in ids.Where(i => _extensions.List.Find(i) is null)) if (await _extensions.InstallFromStore(id, m => ConfirmInstall(m)) is not null) added++;
            said.Add($"{added} extensions");
        }
        return said.Count == 0 ? "Nothing was chosen." : $"From {name}: " + string.Join("; ", said) + ".";
    }

    /// Pages for a space's row: this window's own row if it's the space on screen, one set aside here, or the file.
    private (int Tabs, int Groups) Asleep(Guid space, IReadOnlyList<AsleepPage> pages)
    {
        if (space == _space) return ImportTake.IntoBrowser(_browser, pages);
        if (!_aside.TryGetValue(space, out var aside)) return ImportTake.IntoSession(space, pages);
        // A row set aside here: the pages join it, asleep, as they would the row on screen (without their groups).
        foreach (var page in pages) { var tab = new Tab { Url = page.Url, Title = page.Title, Store = _spaces.StoreName(space) }; aside.Tabs.Add(tab); _pages.Parked.Add(tab); }
        return (pages.Count, 0);
    }

    /// Another browser's passwords file, kept in the vault; the file itself stays yours to delete.
    private string TakePasswordFile(string file)
    {
        var read = PasswordCsv.ReadFile(file);
        if (!read.Recognised) return "That file isn't a passwords export Search knows.";
        var (kept, skipped) = _vault.Take(read.Logins.Select(l => l.ToImported()));
        return $"{kept} passwords brought in" + (skipped + read.Skipped > 0 ? $", {skipped + read.Skipped} skipped or already here" : "") + ". You can delete the file now.";
    }

    private async Task<string?> PickCsv()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
