using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Search.Core;

// Another browser's saved passwords, read from its own files (the passwords half of Import.swift). Plain records come
// out, for the vault to take in; nothing is kept here.
//
// A Chromium browser on Windows keeps them in "Login Data" (SQLite, table logins), each password encrypted with one
// key. That key is in "Local State" (os_crypt.encrypted_key): base64, "DPAPI" and then a blob only this Windows user
// can unwrap (CryptUnprotectData), no question asked. A password marked "v10" or "v11" is AES-256-GCM under it: a
// 12-byte nonce after the mark, the 16-byte tag at the end. One marked "v20" is app-bound (Chrome 127 and later): its
// key is held by Chrome's own elevated service and only Chrome may use it, so it can't be read here; those are
// counted, and the result says to export a CSV from the browser instead. Edge and Brave may still write v10. A
// password from before Chrome 80 is a DPAPI blob of its own. A password lying there in the clear is one only a program
// running as you would have put there, to have Search keep it: not taken.
//
// A Gecko browser keeps them in logins.json, each field base64 DER around a ciphertext, and the key that unlocks them
// wrapped in key4.db. Most people never set a primary password, and that case is read: the empty password is checked
// against key4.db's own record, the key unwrapped, and each login decrypted. With a primary password set, the check
// fails and the result says to export a CSV instead.

/// One saved password from another browser: where it was saved (`Origin`, as the browser had it), the site key it is
/// kept under (`Host`, Accounts.Host's), the name and the password. `Clear`: saved on a plain-http page. Its secret is
/// kept out of ToString, so it never lands in a log.
public sealed record ImportLogin(string Origin, string Host, string User, string Password, bool Clear, DateTimeOffset? Used = null)
{
    public string Id => Host + "\u0001" + User;
    /// As the vault's CSV path takes one in.
    public Imported ToImported() => new(Host, User, Password, Clear);
    private bool PrintMembers(StringBuilder text) { text.Append($"Origin = {Origin}, Host = {Host}, User = {User}, Clear = {Clear}"); return true; }
}

public enum LoginTrouble
{
    None,
    /// Nothing to read: no file, or none that opened.
    Unreadable,
    /// The browser's key couldn't be unwrapped, and nothing decrypted without it.
    NoKey,
    /// A Gecko browser with a primary password set: export from it instead.
    PrimaryPassword,
}

/// What a passwords read found. `Never`: sites the browser was told never to ask about. `Skipped`: passwords with no
/// website to go with them. `Undecrypted`: there, but not unwrapped by the key. `AppBound`: locked to the browser
/// itself (v20); `Advice` then says how to export them instead.
public sealed record FoundLogins(IReadOnlyList<ImportLogin> Logins, IReadOnlyList<string> Never, int Skipped, int Undecrypted, int AppBound, LoginTrouble Trouble, string? Advice)
{
    public static FoundLogins Failed(LoginTrouble trouble, string? advice = null) => new([], [], 0, 0, 0, trouble, advice);
}

public sealed partial class ChromiumSource
{
    /// Every profile's passwords files, or the one profile's. Chrome signed in to a Google account without sync keeps
    /// the ones it saves for that account in a second file beside the first, same shape and same key.
    public IReadOnlyList<string> PasswordFiles(string? only = null) =>
        !ImportsPasswords ? [] : Folders(only).SelectMany(f => new[] { Path.Combine(f, "Login Data"), Path.Combine(f, "Login Data For Account") }).Where(File.Exists).ToList();

    public override FoundLogins Passwords(string? profile = null)
    {
        if (!ImportsPasswords) return FoundLogins.Failed(LoginTrouble.Unreadable);
        var key = Key();
        var logins = new List<ImportLogin>();
        var never = new List<string>();
        var seen = new HashSet<string>();
        int skipped = 0, undecrypted = 0, bound = 0, keyed = 0;
        var readAny = false;
        foreach (var file in PasswordFiles(profile))
        {
            using var copy = Snapshot.Of(file, Root);
            using var db = copy?.Open();
            if (db is null) continue;
            var sql = "SELECT origin_url, username_value, password_value, blacklisted_by_user, date_last_used FROM logins";
            if (!db.Prepares(sql)) sql = "SELECT origin_url, username_value, password_value, blacklisted_by_user, 0 FROM logins";
            if (!db.Prepares(sql)) continue;
            readAny = true;
            foreach (var row in db.Rows(sql))
            {
                var origin = row.Text(0) ?? "";
                var user = row.Text(1) ?? "";
                var blob = row.Blob(2);
                var isNever = row.Long(3) != 0;
                var host = Accounts.Host(origin);
                if (host.Length == 0) { if (!isNever && blob.Length > 0) skipped++; continue; }
                if (isNever) { never.Add(host); continue; }
                if (blob.Length == 0) continue;
                if (Marked(blob, "v20")) { bound++; continue; }
                if (Marked(blob, "v10") || Marked(blob, "v11")) keyed++;
                if (Unwrap(blob, key) is not { } password) { undecrypted++; continue; }
                if (password.Length == 0) continue;
                var login = new ImportLogin(origin, host, user, password, origin.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase), FromChromium(row.Long(4)));
                if (seen.Add(login.Id)) logins.Add(login);
            }
        }
        if (!readAny) return FoundLogins.Failed(LoginTrouble.Unreadable);
        var trouble = key is null && keyed > 0 && logins.Count == 0 ? LoginTrouble.NoKey : LoginTrouble.None;
        var advice = bound > 0 ? $"{bound} of {Name}'s passwords are locked to {Name} itself and can't be read by another app. {ExportSteps}" : null;
        return new(logins, never, skipped, undecrypted, bound, trouble, advice);
    }

    /// The key from Local State: base64, "DPAPI", then the blob only this Windows user can unwrap. Null for none.
    internal byte[]? Key()
    {
        using var doc = Json(ImportRead.TryData(Path.Combine(Root, "Local State"), Root));
        if (doc is null || Text(Get(doc.RootElement, "os_crypt"), "encrypted_key") is not { } text) return null;
        byte[] wrapped;
        try { wrapped = Convert.FromBase64String(text); } catch (FormatException) { return null; }
        return wrapped.Length > 5 && Marked(wrapped, "DPAPI") ? Dpapi.Unprotect(wrapped[5..]) : null;
    }

    private static bool Marked(byte[] blob, string mark) => blob.Length >= mark.Length && blob.AsSpan(0, mark.Length).SequenceEqual(Encoding.ASCII.GetBytes(mark));

    /// The DPAPI blob's own header: version 1 and the provider every blob carries.
    private static readonly byte[] DpapiHeader = [0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB];

    /// "v10"/"v11" and AES-256-GCM under the key, or (before Chrome 80) a DPAPI blob; anything else isn't taken.
    internal static string? Unwrap(byte[] blob, byte[]? key)
    {
        byte[]? plain = null;
        if (Marked(blob, "v10") || Marked(blob, "v11"))
        {
            if (key is not { Length: 32 } || blob.Length < 3 + 12 + 16 || !AesGcm.IsSupported) return null;
            var nonce = blob.AsSpan(3, 12);
            var tag = blob.AsSpan(blob.Length - 16);
            var sealedText = blob.AsSpan(15, blob.Length - 15 - 16);
            plain = new byte[sealedText.Length];
            try { using var aes = new AesGcm(key, 16); aes.Decrypt(nonce, sealedText, tag, plain); }
            catch (CryptographicException) { return null; }
        }
        else if (blob.AsSpan().StartsWith(DpapiHeader)) plain = Dpapi.Unprotect(blob);
        if (plain is null) return null;
        if (Strict(plain) is { } text) return text;
        // Newer builds put a hash of the site before the password. Past it, the password is the same as ever.
        return plain.Length > 32 ? Strict(plain[32..]) : null;
    }

    private static string? Strict(byte[] bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }
}

public sealed partial class GeckoSource
{
    public override FoundLogins Passwords(string? profile = null)
    {
        var logins = new List<ImportLogin>();
        var never = new List<string>();
        var seen = new HashSet<string>();
        int skipped = 0, undecrypted = 0;
        bool readAny = false, locked = false;
        foreach (var folder in Folders(profile))
        {
            var key4 = Path.Combine(folder, "key4.db");
            var store = Path.Combine(folder, "logins.json");
            if (!File.Exists(key4) || !File.Exists(store)) continue;
            var (key, primary) = MasterKey(key4);
            if (primary) { locked = true; continue; }
            if (key is null) continue;
            readAny = true;
            using var doc = Json(ImportRead.TryData(store, Root));
            if (doc is null) continue;
            foreach (var host in Items(Get(doc.RootElement, "disabledHosts")).Select(SwiftJson.Text).OfType<string>())
                if (Accounts.Host(host) is { Length: > 0 } clean) never.Add(clean);
            foreach (var entry in Objects(Get(doc.RootElement, "logins")))
            {
                if (Text(entry, "hostname") is not { } origin) continue;
                var host = Accounts.Host(origin);
                if (host.Length == 0) { skipped++; continue; }
                if (Text(entry, "encryptedUsername") is not { } user64 || Text(entry, "encryptedPassword") is not { } pass64) continue;
                if (DecryptLogin(user64, key) is not { } user || DecryptLogin(pass64, key) is not { } password) { undecrypted++; continue; }
                if (password.Length == 0) continue;
                var used = SwiftJson.Long(Get(entry, "timeLastUsed")) is { } ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(ms, 253402300799999)) : (DateTimeOffset?)null;
                var login = new ImportLogin(origin, host, user, password, origin.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase), used);
                if (seen.Add(login.Id)) logins.Add(login);
            }
        }
        // The primary password is only worth raising when there was nothing else to bring: an open profile still gives its own.
        if (!readAny && locked) return FoundLogins.Failed(LoginTrouble.PrimaryPassword, $"{Name} has a primary password: in {Name}, open about:logins, choose Export passwords from its menu, and bring in the CSV file it saves.");
        if (!readAny) return FoundLogins.Failed(LoginTrouble.Unreadable);
        return new(logins, never, skipped, undecrypted, 0, LoginTrouble.None, null);
    }

    /// The key that unlocks the logins, from key4.db: the empty primary password checked against the browser's own
    /// record first, then the key itself unwrapped from nssPrivate. `Primary` when the check fails.
    private (byte[]? Key, bool Primary) MasterKey(string key4)
    {
        using var copy = Snapshot.Of(key4, Root);
        using var db = copy?.Open();
        if (db is null) return (null, false);
        byte[] salt = [], check = [];
        foreach (var row in db.Rows("SELECT item1, item2 FROM metaData WHERE id = 'password'")) { salt = row.Blob(0); check = row.Blob(1); break; }
        if (salt.Length == 0 || check.Length == 0) return (null, false);
        if (Der.Unwrap(check, salt, []) is not { } said || !said.AsSpan().StartsWith("password-check"u8)) return (null, true);
        // The wrapped key, in the nssPrivate row NSS marks with a fixed id.
        byte[] wantedId = [0xf8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
        var wrapped = db.Rows("SELECT a11, a102 FROM nssPrivate").Select(r => (Key: r.Blob(0), Id: r.Blob(1))).FirstOrDefault(r => r.Id.AsSpan().SequenceEqual(wantedId)).Key;
        return (wrapped is { Length: > 0 } ? Der.Unwrap(wrapped, salt, []) : null, false);
    }

    /// One encrypted login field: base64 around a DER SEQUENCE of the key's id, the cipher with its IV, and the
    /// ciphertext. Newer profiles use AES-256-CBC, older ones 3DES-CBC; the DER says which.
    internal static string? DecryptLogin(string b64, byte[] master)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(b64); } catch (FormatException) { return null; }
        if (Der.Read(blob, 0) is not { } outer || Der.Items(outer.Body) is not { Count: 3 } parts || Der.Items(parts[1].Body) is not { Count: 2 } cipher) return null;
        var iv = cipher[1].Body;
        var plain = Der.Oid(cipher[0].Body) == Der.Aes256Cbc
            ? Der.Cbc(parts[2].Body, Aes.Create(), master, iv.Length == 14 ? [0x04, 0x0e, .. iv] : iv)
            : Der.Cbc(parts[2].Body, TripleDES.Create(), master.Length >= 24 ? master[..24] : master, iv);
        if (plain is null) return null;
        try { return new UTF8Encoding(false, true).GetString(plain); } catch (DecoderFallbackException) { return null; }
    }
}

/// The little of DER that NSS's key and login structures need, and the two ways a key4.db entry is protected.
internal static class Der
{
    public const string Aes256Cbc = "2.16.840.1.101.3.4.1.42", Pbes2 = "1.2.840.113549.1.5.13", PbeSha1TripleDes = "1.2.840.113549.1.12.5.1.3";

    public readonly record struct Element(byte Tag, byte[] Body, int Next);

    /// One element at an offset: its tag, its content, and where the next begins.
    public static Element? Read(byte[] b, int i)
    {
        if (i + 1 >= b.Length) return null;
        var tag = b[i];
        var j = i + 1;
        var length = (int)b[j++];
        if ((length & 0x80) != 0)
        {
            // At most four bytes of length: nothing in key4.db is longer, and more would overflow.
            var n = length & 0x7f;
            if (n is 0 or > 4 || j + n > b.Length) return null;
            long total = 0;
            for (var k = 0; k < n; k++) total = total << 8 | b[j++];
            if (total > int.MaxValue) return null;
            length = (int)total;
        }
        if (length < 0 || j + (long)length > b.Length) return null;
        return new Element(tag, b[j..(j + length)], j + length);
    }

    /// The elements of a constructed value, in order.
    public static List<Element> Items(byte[] body)
    {
        var items = new List<Element>();
        for (var i = 0; Read(body, i) is { } one; i = one.Next) items.Add(one);
        return items;
    }

    /// An object identifier as its dotted string.
    public static string Oid(byte[] body)
    {
        if (body.Length == 0) return "";
        var parts = new List<long> { body[0] / 40, body[0] % 40 };
        long value = 0;
        foreach (var b in body.Skip(1))
        {
            value = value << 7 | (long)(b & 0x7f);
            if ((b & 0x80) == 0) { parts.Add(value); value = 0; }
        }
        return string.Join('.', parts);
    }

    /// An unsigned integer, as key4.db's are small.
    public static long Integer(byte[] body) => body.Aggregate(0L, (n, b) => n << 8 | b);

    /// A key4.db entry, the check or the wrapped key, unwrapped with `password`: PBES2 (PBKDF2-HMAC-SHA256 then
    /// AES-256-CBC) in current Firefox, PBE-SHA1-3DES before that.
    public static byte[]? Unwrap(byte[] blob, byte[] globalSalt, byte[] password)
    {
        if (Read(blob, 0) is not { } outer || Items(outer.Body) is not { Count: 2 } top || Items(top[0].Body) is not { Count: 2 } algo) return null;
        var ciphertext = top[1].Body;
        var oid = Oid(algo[0].Body);
        if (oid == Pbes2)
        {
            // NSS feeds PBKDF2 the SHA-256 of the salt and the password; the real IV is the fourteen bytes it kept
            // behind a DER octet-string header.
            if (Items(algo[1].Body) is not { Count: 2 } parameters || Items(parameters[0].Body) is not { Count: 2 } kdf || Items(kdf[1].Body) is not { Count: >= 2 } kp) return null;
            var entrySalt = kp[0].Body;
            // A damaged file's count could be anything; NSS writes 10,000.
            var rounds = kp[1].Body.Length <= 4 ? Integer(kp[1].Body) : 0;
            if (rounds is < 1 or > 10_000_000 || entrySalt.Length == 0) return null;
            if (Items(parameters[1].Body) is not { Count: 2 } encryption) return null;
            var key = Rfc2898DeriveBytes.Pbkdf2(SHA256.HashData([.. globalSalt, .. password]), entrySalt, (int)rounds, HashAlgorithmName.SHA256, 32);
            return Cbc(ciphertext, Aes.Create(), key, [0x04, 0x0e, .. encryption[1].Body]);
        }
        if (oid == PbeSha1TripleDes)
        {
            // A chain of SHA-1 and HMAC-SHA-1 over the salts ends in the 3DES key and its IV.
            if (Items(algo[1].Body) is not { Count: > 0 } parameters) return null;
            var entrySalt = parameters[0].Body;
            var hp = SHA1.HashData([.. globalSalt, .. password]);
            var chp = SHA1.HashData([.. hp, .. entrySalt]);
            byte[] pes = entrySalt.Length < 20 ? [.. entrySalt, .. new byte[20 - entrySalt.Length]] : entrySalt[..20];
            byte[] first = [.. pes, .. entrySalt];
            var k1 = HMACSHA1.HashData(chp, first);
            var tk = HMACSHA1.HashData(chp, pes);
            byte[] second = [.. tk, .. entrySalt];
            var k2 = HMACSHA1.HashData(chp, second);
            byte[] k = [.. k1, .. k2];
            return Cbc(ciphertext, TripleDES.Create(), k[..24], k[^8..]);
        }
        return null;
    }

    /// CBC decryption with PKCS#7 padding stripped; null for a key or IV that doesn't fit, or a wrong key.
    public static byte[]? Cbc(byte[] data, SymmetricAlgorithm cipher, byte[] key, byte[] iv)
    {
        using (cipher)
        {
            if (data.Length == 0 || iv.Length != cipher.BlockSize / 8) return null;
            try { cipher.Key = key; return cipher.DecryptCbc(data, iv, PaddingMode.PKCS7); }
            catch (CryptographicException) { return null; }
        }
    }
}

/// DPAPI, as Chromium uses it to keep its key: CryptUnprotectData for this Windows user, no prompt.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    private const int NoPrompt = 0x1;

    public static byte[]? Unprotect(byte[] data) => Call(data, unprotect: true);

    /// For tests: a blob as Chromium would have written it.
    public static byte[]? Protect(byte[] data) => Call(data, unprotect: false);

    private static byte[]? Call(byte[] data, bool unprotect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(data.Length, 1)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = unprotect ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, ref output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, ref output);
            if (!ok || output.Data == IntPtr.Zero) return null;
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, output.Size);
            return bytes;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
