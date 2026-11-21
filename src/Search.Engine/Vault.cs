using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Engine;

// Where passwords live (Vault.swift). The Mac keeps them in its keychain; Windows has no drawer of that kind a browser
// can call its own, so they are one file in the store folder, sealed for the person signed in to Windows (DPAPI,
// CurrentUser) and nobody else. Sealed twice: the file as a whole, so not even which sites you have accounts on can be
// read off the disk, and each password again inside it, so a list (the panel, the rows under a sign-in box) never
// holds one in the clear: a secret is unsealed only for the one account picked, shown or copied. Nothing is written
// in any other form, and nothing is ever logged. A test world has its own file for free (Store).
//
// Called from the UI thread and from the thread pool alike: everything is behind one lock, and the file is written on
// Disk's queue, the newest write last.

/// One account with its secret, for the paths that hand a password over: filling a sign-in, and an offer to save one.
/// Its ToString leaves the password out.
public sealed record Login(string Host, string User, string Password, DateTimeOffset? Used = null, bool Clear = false)
{
    private bool PrintMembers(StringBuilder text) { text.Append($"Host = {Host}, User = {User}, Used = {Used}, Clear = {Clear}"); return true; }
}

public sealed class Vault
{
    public const string FileName = "passwords.dat";
    /// Mixed into each seal, so a blob some other program sealed for the same Windows account doesn't open as ours.
    private static readonly byte[] _fileEntropy = "Search browser passwords file v1"u8.ToArray();
    private static readonly byte[] _secretEntropy = "Search browser password v1"u8.ToArray();

    private sealed class Entry
    {
        public required string Host, User;
        /// The password, sealed. Unsealed only by Secret().
        public required byte[] Sealed;
        public DateTimeOffset Created;
        public DateTimeOffset? Used;
        public bool Clear;
        public Account Row => new(Host, User, Created, Used, Clear);
    }

    private readonly Lock _lock = new();
    private readonly string _file;
    private List<Entry>? _entries;
    private HashSet<string> _never = [];

    /// Something kept changed: saved, updated, used, removed, or a site told never to ask.
    public event Action? Changed;

    public Vault(string file = FileName) => _file = Store.File(file);

    // Reading. Nothing here unseals a password but Secret and Login.

    /// Everything kept, as the panel lists it (by site, then name): no secrets.
    public IReadOnlyList<Account> All() { lock (_lock) return Accounts.Sorted(Entries.Select(e => e.Row)); }

    /// What is kept for exactly this host.
    public IReadOnlyList<Account> KeptFor(string host) { lock (_lock) return Entries.Where(e => e.Host == host).Select(e => e.Row).ToList(); }

    /// What is kept for a site: the host's own, then the rest of its site's (see Accounts.Matching).
    public IReadOnlyList<Account> Matching(string host, bool wide = true) => Accounts.Matching(All(), host, wide);

    /// The accounts to hang from a sign-in box on a page of `host` (see Accounts.Offered).
    public IReadOnlyList<Account> Offered(string host, bool wide = true, bool clear = false, string? given = null) => Accounts.Offered(All(), host, wide, clear, given);

    /// The account last used to sign in to a site, if any was ever used.
    public Account? LastUsed(string host) => Matching(host).Where(a => a.Used is not null).MaxBy(a => a.Used);

    /// One account's password: read when it is filled in, shown or copied, never as part of a list.
    public string? Secret(Account account)
    {
        byte[]? sealedSecret;
        lock (_lock) sealedSecret = Find(account.Host, account.User)?.Sealed;
        if (sealedSecret is null) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedSecret, _secretEntropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    /// One account with its secret, for the one picked.
    public Login? Login(Account account) => Secret(account) is { } password ? new Login(account.Host, account.User, password, account.Used, account.Clear) : null;

    // Writing.

    /// Kept, or the account kept under that host and name updated: its password, whether it came over plain http, and
    /// when it was used if that is given (left as it was otherwise). Nothing without a host or a password.
    public bool Save(string host, string user, string password, DateTimeOffset? used = null, bool clear = false)
    {
        if (host.Length == 0 || password.Length == 0) return false;
        var sealedSecret = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), _secretEntropy, DataProtectionScope.CurrentUser);
        lock (_lock)
        {
            if (Find(host, user) is { } entry) { entry.Sealed = sealedSecret; entry.Clear = clear; if (used is not null) entry.Used = used; }
            else Entries.Add(new Entry { Host = host, User = user, Sealed = sealedSecret, Created = DateTimeOffset.UtcNow, Used = used, Clear = clear });
            Write();
        }
        Changed?.Invoke();
        return true;
    }

    /// A new password for an account already kept, its dates and the rest as they were.
    public bool Update(Account account, string password)
    {
        lock (_lock) if (Find(account.Host, account.User) is null) return false;
        return Save(account.Host, account.User, password, null, account.Clear);
    }

    /// It was just used to sign in: lists put it first from now on. `clear`: where it was last used is where it is
    /// offered from (plain http or not).
    public void Touch(Account account, bool? clear = null)
    {
        lock (_lock)
        {
            if (Find(account.Host, account.User) is not { } entry) return;
            entry.Used = DateTimeOffset.UtcNow;
            entry.Clear = clear ?? entry.Clear;
            Write();
        }
        Changed?.Invoke();
    }

    public void Touch(Login login) => Touch(new Account(login.Host, login.User, default, login.Used, login.Clear), login.Clear);

    /// Whether it was there to let go of: Remove All… counts only those.
    public bool Forget(string host, string user)
    {
        lock (_lock)
        {
            if (Find(host, user) is not { } entry) return false;
            Entries.Remove(entry);
            Write();
        }
        Changed?.Invoke();
        return true;
    }

    /// Several at once, in one write: how many were let go of.
    public int Forget(IEnumerable<Account> accounts)
    {
        int gone;
        lock (_lock)
        {
            var wanted = accounts.Select(a => a.Id).ToHashSet();
            gone = Entries.RemoveAll(e => wanted.Contains(e.Host + "\u0001" + e.User));
            if (gone > 0) Write();
        }
        if (gone > 0) Changed?.Invoke();
        return gone;
    }

    /// A sign-in that worked (Forms.SignedIn), as the Mac takes one: the same account with the same password is only
    /// marked used, and offered nothing; otherwise it is the person's to say whether to keep it (New) or update the
    /// one kept (Changed), and nothing is written until they say so (Save). Known also when nothing is to be asked: a
    /// site told never to, or no password.
    public Saving Consider(string host, string user, string password, bool clear)
    {
        if (password.Length == 0 || IsNever(host)) return Saving.Known;
        var kept = KeptFor(host);
        var verdict = Accounts.Judge(kept, host, user, password, Secret);
        if (verdict == Saving.Known) Touch(kept.First(a => a.User == user), clear);
        return verdict;
    }

    // Sites that asked not to be asked.

    public IReadOnlySet<string> Never
    {
        get { lock (_lock) { _ = Entries; return _never.ToHashSet(); } }
        set { lock (_lock) { _ = Entries; _never = value.ToHashSet(); Write(); } Changed?.Invoke(); }
    }

    public void NeverFor(string host) { lock (_lock) { _ = Entries; if (!_never.Add(host)) return; Write(); } Changed?.Invoke(); }

    public bool IsNever(string host) => Accounts.IsNever(Never, host);

    /// A passwords export (see Accounts.FromCsv), put in here: how many were kept and how many skipped. Read once; the
    /// file is yours to delete, and no copy of it is kept.
    public (int Kept, int Skipped) Take(string csv)
    {
        var (rows, skipped) = Accounts.FromCsv(csv);
        var kept = 0;
        foreach (var row in rows) { if (Save(row.Host, row.User, row.Password, clear: row.Clear)) kept++; else skipped++; }
        return (kept, skipped);
    }

    // The file: DPAPI(UTF-8 JSON { version, logins: [{ host, user, secret, created, used, clear }], never: [] }),
    // each secret itself DPAPI-sealed and in base64, dates in seconds since 1970 as the Mac writes them.

    private List<Entry> Entries => _entries ??= Load();

    private Entry? Find(string host, string user) => Entries.FirstOrDefault(e => e.Host == host && e.User == user);

    private List<Entry> Load()
    {
        if (!File.Exists(_file)) return [];
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_file), _fileEntropy, DataProtectionScope.CurrentUser);
            var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException();
            _never = (root["never"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet();
            return (root["logins"] as JsonArray ?? []).OfType<JsonObject>().Select(Read).OfType<Entry>().ToList();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            // Sealed for another Windows account or machine, or damaged: set aside, never written over.
            Store.Quarantine(_file);
            return [];
        }
    }

    private static Entry? Read(JsonObject o)
    {
        if (o["host"]?.GetValue<string>() is not { Length: > 0 } host || o["secret"]?.GetValue<string>() is not { Length: > 0 } secret) return null;
        return new Entry
        {
            Host = host, User = o["user"]?.GetValue<string>() ?? "", Sealed = Convert.FromBase64String(secret),
            Created = Date(o["created"]) ?? DateTimeOffset.UnixEpoch, Used = Date(o["used"]), Clear = o["clear"]?.GetValue<bool>() ?? false
        };
    }

    private static DateTimeOffset? Date(JsonNode? node) => node?.GetValue<double>() is { } seconds ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : null;
    private static double Seconds(DateTimeOffset date) => (date - DateTimeOffset.UnixEpoch).TotalSeconds;

    /// Under the lock: what is kept now, encoded here and sealed and written on Disk's queue.
    private void Write()
    {
        var logins = new JsonArray(Entries.Select(e => (JsonNode)new JsonObject
        {
            ["host"] = e.Host, ["user"] = e.User, ["secret"] = Convert.ToBase64String(e.Sealed),
            ["created"] = Seconds(e.Created), ["used"] = e.Used is { } used ? Seconds(used) : null, ["clear"] = e.Clear
        }).ToArray());
        var root = new JsonObject { ["version"] = 1, ["logins"] = logins, ["never"] = new JsonArray(_never.Order(StringComparer.Ordinal).Select(h => (JsonNode)h).ToArray()) };
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        _ = Disk.Write(_file, () => ProtectedData.Protect(json, _fileEntropy, DataProtectionScope.CurrentUser));
    }

    /// Waits for the file to be written: for tests, and on the way out.
    public static Task Flush() => Disk.Flush();
}
