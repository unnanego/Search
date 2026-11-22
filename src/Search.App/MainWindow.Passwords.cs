using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Search.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials.UI;

namespace Search.App;

// Passwords in the window (Accounts.swift, Passwords.swift): your accounts under the sign-in box the caret is in, the
// way Safari does it, never filling anything on its own; an offer to keep a sign-in once it has actually worked; and
// the passwords panel. The vault (Vault.cs) keeps them encrypted for your Windows account; the page's own scripts
// never see one except the moment it goes into the box you chose.
public sealed partial class MainWindow
{
    private readonly Vault _vault = App.Shared.Vault;
    private Forms? _forms;
    private (Tab Tab, FieldInfo Field, string Host, bool Clear)? _field;
    private DateTime _loginsShown;
    private Tab? _filledUntilLeft;
    private (Tab Tab, SignIn SignIn)? _keeping;

    private void WirePasswords()
    {
        _forms = new Forms(_pages);
        _pages.Loaded += tab => _ = _forms.Arrived(tab);
        _pages.Released += tab => _forms.Closed(tab);
        _forms.FieldEntered += (tab, field) => DispatcherQueue.TryEnqueue(() => OnField(tab, field));
        _forms.FieldLeft += tab => DispatcherQueue.TryEnqueue(async () => { await Task.Delay(200); if (_field?.Tab == tab) HideLogins(); if (_filledUntilLeft == tab) _filledUntilLeft = null; });
        _forms.SignedIn += (tab, signIn) => DispatcherQueue.TryEnqueue(() => OfferToKeep(tab, signIn));
        LoginRows.ItemClick += (_, e) => { if (e.ClickedItem is PanelRow { Item: Account account }) _ = Pick(account); };
        _browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.Selected)) { HideLogins(); HideKeeping(); } };
    }

    /// Why the last field had no list under it, for the bench.
    private string _fieldNote = "";

    private void OnField(Tab tab, FieldInfo field)
    {
        _fieldNote = $"field {field}";
        if (tab != _browser.Selected || tab == _filledUntilLeft || Accounts.Host(tab.Url) is not { } host || _pages.ViewOf(tab) is not { } view) { _fieldNote += " | not this tab, just filled, or no site"; HideLogins(); return; }
        var clear = tab.Url!.Scheme == "http";
        if (!field.Passwords && !field.NameOnly) { _fieldNote += " | not a sign-in box"; HideLogins(); return; }
        var offered = _vault.Offered(host, field.Wide, clear, _forms!.GivenName(tab));
        if (offered.Count == 0) { _fieldNote += $" | nothing kept for {host} (clear={clear}, all={_vault.All().Count})"; HideLogins(); return; }
        var (own, others) = Accounts.Split(offered, host);
        var rows = own.Select(a => LoginRow(a, host)).ToList();
        if (others.Count > 0) rows.AddRange(others.Select(a => LoginRow(a, $"on {a.Host}")));
        LoginRows.ItemsSource = rows;
        // Under the box, or over it when there's no room below.
        var box = field.InDips(view.XamlRoot.RasterizationScale);
        var origin = view.TransformToVisual(Overlay).TransformPoint(new Windows.Foundation.Point(0, 0));
        var below = origin.Y + box.Y + box.Height + 4;
        var room = Overlay.ActualHeight - below;
        var top = room < 140 && origin.Y + box.Y > 140 ? origin.Y + box.Y - 4 - Math.Min(260, rows.Count * 52 + 8) : below;
        Canvas.SetLeft(Logins, Math.Clamp(origin.X + box.X, 0, Math.Max(0, Overlay.ActualWidth - 300)));
        Canvas.SetTop(Logins, Math.Max(0, top));
        if (Logins.Visibility != Visibility.Visible) _loginsShown = DateTime.UtcNow;
        Logins.Visibility = Visibility.Visible;
        _field = (tab, field, host, clear);
    }

    private static PanelRow LoginRow(Account account, string detail) => new() { Item = account, Title = account.User.Length > 0 ? account.User : "(no name)", Detail = detail };

    private void HideLogins() { Logins.Visibility = Visibility.Collapsed; LoginRows.ItemsSource = null; _field = null; }

    private async Task Pick(Account account)
    {
        // A click that lands as the list appears was meant for the page.
        if (_field is not { } field || DateTime.UtcNow - _loginsShown < TimeSpan.FromMilliseconds(500)) return;
        HideLogins();
        if (field.Tab != _browser.Selected || Accounts.Host(field.Tab.Url) != field.Host || (field.Tab.Url!.Scheme == "http") != field.Clear) return;
        bool filled;
        if (field.Field.NameOnly) filled = await _forms!.FillName(field.Tab, account.User);
        else
        {
            if (_vault.Login(account) is not { } login) return;
            filled = await _forms!.Fill(field.Tab, login.User, login.Password);
            if (filled) _vault.Touch(login);
        }
        _filledUntilLeft = field.Tab;
        if (!filled) Notice("Couldn't find the sign-in fields anymore.");
    }

    private void OfferToKeep(Tab tab, SignIn signIn)
    {
        if (!_prefs.SavePasswords || tab.Bench || tab.Private) return;
        var verdict = _vault.Consider(signIn.Host, signIn.User, signIn.Password, signIn.Clear);
        if (verdict == Saving.Known) return;
        _keeping = (tab, signIn);
        var who = signIn.User.Length > 0 ? signIn.User : "this account";
        KeepingText.Text = verdict == Saving.Changed ? $"Update the password for {who} on {signIn.Host}?" : $"Save the password for {who} on {signIn.Host}?";
        KeepingSave.Content = verdict == Saving.Changed ? "Update" : "Save";
        KeepingButtons.Visibility = Visibility.Visible;
        _notices++;
        Keeping.Visibility = Visibility.Visible;
    }

    private void OnKeeping(object sender, RoutedEventArgs e) => AnswerKeeping(sender is FrameworkElement { Tag: string answer } ? answer : "later");

    private void AnswerKeeping(string answer)
    {
        if (_keeping is { } keeping)
        {
            if (answer == "save") _vault.Save(keeping.SignIn.Host, keeping.SignIn.User, keeping.SignIn.Password, DateTimeOffset.UtcNow, keeping.SignIn.Clear);
            else if (answer == "never") _vault.NeverFor(keeping.SignIn.Host);
        }
        HideKeeping();
    }

    // The offer holds the password only as long as it's shown.
    private void HideKeeping() { _keeping = null; Keeping.Visibility = Visibility.Collapsed; }

    /// A line said in the corner: gone after a few seconds, or kept until HideNotice when it's about something ongoing.
    private int _notices;
    private void Notice(string text, bool stay = false)
    {
        KeepingText.Text = text;
        _keeping = null;
        KeepingButtons.Visibility = Visibility.Collapsed;
        Keeping.Visibility = Visibility.Visible;
        var mine = ++_notices;
        if (!stay) _ = HideNoticeSoon(mine);
    }

    private async Task HideNoticeSoon(int notice) { await Task.Delay(3000); if (notice == _notices) HideNotice(); }

    private void HideNotice() { if (_keeping is not null) return; Keeping.Visibility = Visibility.Collapsed; KeepingButtons.Visibility = Visibility.Visible; }

    // The passwords panel: every site and its accounts; a click copies the password, after Windows Hello.
    private List<PanelRow> PasswordRows(string words) =>
        Accounts.Sites(_vault.All(), words).SelectMany(site => site.Accounts.Select(a => new PanelRow
        {
            Item = a, Glyph = "", Title = site.Host, Detail = (a.User.Length > 0 ? a.User : "(no name)") + (a.Used is { } used ? " · used " + When(used) : ""),
            ActionGlyph = "", ActionName = "Forget"
        })).ToList();

    private async Task CopyPassword(Account account)
    {
        if (!Store.Testing && await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available
            && await UserConsentVerifier.RequestVerificationAsync($"Copy the password for {account.User} on {account.Host}") != UserConsentVerificationResult.Verified) return;
        if (_vault.Secret(account) is not { } secret) return;
        var package = new DataPackage();
        package.SetText(secret);
        Clipboard.SetContent(package);
        _vault.Touch(account);
    }

    private async Task ForgetPassword(Account account)
    {
        var sure = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Forget this password?", Content = $"{account.User} on {account.Host}. It can't be brought back.", PrimaryButtonText = "Forget", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
        if (await sure.ShowAsync() == ContentDialogResult.Primary) { _vault.Forget(account.Host, account.User); FillPanel(); }
    }
}
