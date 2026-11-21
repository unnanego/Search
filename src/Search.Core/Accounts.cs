using System.Text;

namespace Search.Core;

// Which saved accounts a page is offered, in what order, and what a sign-in that worked does to them (the pure half
// of Vault.swift, Accounts.swift and Browser.hang/onCredentials). No secrets here: an Account is a row as a list needs
// it, and the one password ever read for a list is the one picked.

/// One kept account without its secret (Kept in Vault.swift). `Host` is the site key: lowercase, no www. `Clear`: kept
/// from a page sent over plain http; only those are offered on such a page, never one kept from https.
public sealed record Account(string Host, string User, DateTimeOffset Created, DateTimeOffset? Used, bool Clear)
{
    public string Id => Host + "\u0001" + User;
}

/// What a sign-in that worked means for what is kept: nothing to ask (`Known`, the same password: only marked used),
/// a new account to offer saving (`New`), or a new password for one kept (`Changed`, offered as an update).
public enum Saving { Known, New, Changed }

/// One site's accounts in the passwords panel.
public sealed record Site(string Host, IReadOnlyList<Account> Accounts);

/// One row of a passwords export, its secret in it: hidden from ToString, so it never lands in a log.
public sealed record Imported(string Host, string User, string Password, bool Clear)
{
    private bool PrintMembers(StringBuilder text) { text.Append($"Host = {Host}, User = {User}, Clear = {Clear}"); return true; }
}

public static class Accounts
{
    /// Accounts offered under one box at most.
    public const int Most = 5;

    /// The site key a page is kept and offered under: its host, lowercase, without www. Websites only.
    public static string? Host(Uri? page)
    {
        if (page is null || !page.IsAbsoluteUri || page.Scheme is not ("http" or "https") || page.IdnHost is not { Length: > 0 } host) return null;
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// The site key of something typed or exported: "example.com", "https://www.example.com/login". A website's and
    /// nothing else: an Android app's login in an export (android://…@com.vendor.app/) names a package, which can read
    /// as a domain somebody else owns.
    public static string Host(string text)
    {
        var value = text.Trim();
        if (!value.Contains("://")) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var url) ? Host(url) ?? "" : "";
    }

    /// Whether an account kept for `server` is offered on `host`: the same host, or with `wide`, another host of the
    /// same site. Without a list of public suffixes, only the same host.
    public static bool Belongs(string server, string host, bool wide = true) => Belongs(server, host, wide, Registrable.Suffixes);

    internal static bool Belongs(string server, string host, bool wide, Func<string, bool>? isSuffix) =>
        server == host || (wide && Registrable.SameSite(server, host, isSuffix));

    /// What is kept for a site, as Vault.kept(matching:): the host's own accounts first, then the rest of its site's
    /// (unless `wide` is false: a box that only might be a sign-in's gets this host's alone); each group newest first.
    public static IReadOnlyList<Account> Matching(IEnumerable<Account> kept, string host, bool wide = true) => Matching(kept, host, wide, Registrable.Suffixes);

    internal static IReadOnlyList<Account> Matching(IEnumerable<Account> kept, string host, bool wide, Func<string, bool>? isSuffix)
    {
        var all = kept as IReadOnlyCollection<Account> ?? kept.ToList();
        var exact = all.Where(a => a.Host == host).OrderByDescending(Last);
        var wider = wide ? all.Where(a => a.Host != host && Belongs(a.Host, host, true, isSuffix)).OrderByDescending(Last) : Enumerable.Empty<Account>();
        return exact.Concat(wider).ToList();
    }

    /// The accounts hung from a sign-in box on a page of `host`. `clear`: the page came over plain http, and could have
    /// been written by anyone on the way (a café's network), so it gets only what was kept from plain http. `given`:
    /// the name the step before was sent with, whose account comes first on the page that asks for its password.
    public static IReadOnlyList<Account> Offered(IEnumerable<Account> kept, string host, bool wide = true, bool clear = false, string? given = null) =>
        Offered(kept, host, wide, clear, given, Registrable.Suffixes);

    internal static IReadOnlyList<Account> Offered(IEnumerable<Account> kept, string host, bool wide, bool clear, string? given, Func<string, bool>? isSuffix)
    {
        var list = Matching(kept, host, wide, isSuffix).Where(a => !clear || a.Clear).ToList();
        var at = given is { Length: > 0 } ? list.FindIndex(a => string.Equals(a.User, given, StringComparison.OrdinalIgnoreCase)) : -1;
        if (at > 0) { var first = list[at]; list.RemoveAt(at); list.Insert(0, first); }
        return list.Take(Most).ToList();
    }

    /// The rows as drawn: this host's own first, then, under "Other accounts on <its site>", the rest of its site's,
    /// so a login kept for example.com is there on admin.example.com without passing for admin's.
    public static (IReadOnlyList<Account> Own, IReadOnlyList<Account> Others) Split(IReadOnlyList<Account> offered, string host) =>
        (offered.Where(a => a.Host == host).ToList(), offered.Where(a => a.Host != host).ToList());

    /// What a sign-in that worked on `host` as `user` means, against what is kept. Only that account's secret is read
    /// (`secret`), never every one kept for the site. Kept by host and name exactly, as the keychain keys them.
    public static Saving Judge(IEnumerable<Account> kept, string host, string user, string password, Func<Account, string?> secret)
    {
        var same = kept.FirstOrDefault(a => a.Host == host && a.User == user);
        if (same is null) return Saving.New;
        return secret(same) == password ? Saving.Known : Saving.Changed;
    }

    /// Whether a site asked never to be asked: its host, or its whole site.
    public static bool IsNever(IReadOnlySet<string> never, string host) => never.Contains(host) || never.Contains(Registrable.Domain(host));

    /// Everything kept, as the panel lists it: by site, then by name.
    public static IReadOnlyList<Account> Sorted(IEnumerable<Account> kept) =>
        kept.OrderBy(a => a.Host, StringComparer.Ordinal).ThenBy(a => a.User, StringComparer.Ordinal).ToList();

    /// The panel's sites, narrowed by what was typed into its search: a site or an account containing it.
    public static IReadOnlyList<Site> Sites(IEnumerable<Account> kept, string needle = "")
    {
        var want = needle.Trim().ToLowerInvariant();
        var rows = want.Length == 0 ? kept : kept.Where(a => a.Host.Contains(want, StringComparison.Ordinal) || a.User.ToLowerInvariant().Contains(want, StringComparison.Ordinal));
        return rows.GroupBy(a => a.Host).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Site(g.Key, g.OrderBy(a => a.User, StringComparer.Ordinal).ToList())).ToList();
    }

    private static DateTimeOffset Last(Account a) => a.Used ?? DateTimeOffset.MinValue;

    /// A CSV as Google Password Manager, Chrome, Edge or Dia write it (name, url, username, password, note): the rows
    /// that name a website and a password, and how many didn't.
    public static (IReadOnlyList<Imported> Rows, int Skipped) FromCsv(string text)
    {
        var rows = ParseCsv(text);
        if (rows.Count == 0) return ([], 0);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(params string[] names) => header.FindIndex(names.Contains);
        int urlAt = Column("url", "login_uri", "website", "site"), userAt = Column("username", "login_username", "user", "email"), passAt = Column("password", "login_password");
        if (urlAt < 0 || userAt < 0 || passAt < 0) return ([], rows.Count - 1);
        var kept = new List<Imported>();
        var skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= Math.Max(urlAt, Math.Max(userAt, passAt))) { skipped++; continue; }
            var host = Host(row[urlAt]);
            if (host.Length == 0 || row[passAt].Length == 0) { skipped++; continue; }
            kept.Add(new Imported(host, row[userAt], row[passAt], row[urlAt].Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
        }
        return (kept, skipped);
    }

    /// Quoted fields, doubled quotes inside them, and newlines inside those: all three turn up in a real export.
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        void EndRow() { row.Add(field.ToString()); field.Clear(); if (row.Any(f => f.Length > 0)) rows.Add(row); row = []; }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n': break;
                case '\n' or '\r': EndRow(); break;
                default: field.Append(c); break;
            }
        }
        EndRow();
        return rows;
    }
}
