using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// A made-up %LOCALAPPDATA% and %APPDATA% in a temp folder of its own, for the import readers: no test ever reads a
/// real browser's files. Files, SQLite databases, SNSS, .jsonlz4 and DER are all built here.
internal sealed class ImportFixture : IDisposable
{
    public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"search-core-import-tests-{Guid.NewGuid():N}");
    public string Local => System.IO.Path.Combine(Folder, "Local");
    public string Roaming => System.IO.Path.Combine(Folder, "Roaming");
    public string ArcFolder => System.IO.Path.Combine(Folder, "Local", "Packages", "TheBrowserCompany.Arc_test", "LocalCache", "Local", "Arc");
    public ImportRoots Roots => new(Local, Roaming);

    public ImportFixture() { Directory.CreateDirectory(Local); Directory.CreateDirectory(Roaming); }

    public string Path(params string[] parts) => System.IO.Path.Combine([Folder, .. parts]);

    public string Write(string file, string text) => Write(file, Encoding.UTF8.GetBytes(text));

    public string Write(string file, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
        return file;
    }

    /// A SQLite file made with the readers' own wrapper.
    public static string Db(string file, params string[] sql)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        using var db = Sqlite.Open(file, readOnly: false) ?? throw new InvalidOperationException("winsqlite3 didn't open");
        foreach (var statement in sql) db.Exec(statement);
        return file;
    }

    public static void Run(string file, string sql, params object?[] values)
    {
        using var db = Sqlite.Open(file, readOnly: false) ?? throw new InvalidOperationException("winsqlite3 didn't open");
        db.Run(sql, values);
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Little-endian pieces.

    public static byte[] I32(int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); return b; }
    public static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Pad(int length) => new byte[(4 - length % 4) % 4];

    // SNSS: Chromium's session file.

    public static byte[] Snss(params (byte Id, byte[] Body)[] frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange("SNSS"u8.ToArray());
        bytes.AddRange(I32(1));
        foreach (var (id, body) in frames)
        {
            var size = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)(body.Length + 1));
            bytes.AddRange(size);
            bytes.Add(id);
            bytes.AddRange(body);
        }
        return bytes.ToArray();
    }

    public static byte[] Str8(string text) { var b = Encoding.UTF8.GetBytes(text); return Join(I32(b.Length), b, Pad(b.Length)); }
    public static byte[] Str16(string text) { var b = Encoding.Unicode.GetBytes(text); return Join(I32(text.Length), b, Pad(b.Length)); }
    public static byte[] Pickle(byte[] payload) => Join(I32(payload.Length), payload);

    public static (byte, byte[]) Window(int window, int tab) => (0, Join(I32(window), I32(tab)));
    public static (byte, byte[]) Index(int tab, int index) => (2, Join(I32(tab), I32(index)));
    public static (byte, byte[]) Navigation(int tab, int index, string url, string title) => (6, Pickle(Join(I32(tab), I32(index), Str8(url), Str16(title), new byte[12])));
    public static (byte, byte[]) Selected(int tab, int index) => (7, Join(I32(tab), I32(index)));
    public static (byte, byte[]) WindowType(int window, int type) => (9, Join(I32(window), I32(type)));
    public static (byte, byte[]) Pinned(int tab, bool on) => (12, Join(I32(tab), [(byte)(on ? 1 : 0), 0, 0, 0]));
    public static (byte, byte[]) Closed(int tab) => (16, I32(tab));
    public static (byte, byte[]) Group(int tab, ulong high, ulong low, bool on = true) => (25, Join(I32(tab), new byte[4], U64(high), U64(low), [(byte)(on ? 1 : 0), 0, 0, 0, 0, 0, 0, 0]));
    public static (byte, byte[]) GroupMeta(ulong high, ulong low, string title, bool collapsed) => (27, Pickle(Join(U64(high), U64(low), Str16(title), I32(3), I32(collapsed ? 1 : 0))));

    // Mozilla's .jsonlz4: the magic, the size, one LZ4 block of literals alone (as valid as any).

    public static byte[] Lz4Literals(byte[] data)
    {
        var block = new List<byte>();
        if (data.Length < 15) block.Add((byte)(data.Length << 4));
        else
        {
            block.Add(0xF0);
            var rest = data.Length - 15;
            while (rest >= 255) { block.Add(255); rest -= 255; }
            block.Add((byte)rest);
        }
        block.AddRange(data);
        return block.ToArray();
    }

    public static byte[] JsonLz4(string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        return Join("mozLz40\0"u8.ToArray(), I32(data.Length), Lz4Literals(data));
    }

    // DER, as NSS writes key4.db and logins.json.

    public static byte[] Tlv(byte tag, params byte[][] parts)
    {
        var body = Join(parts);
        byte[] length = body.Length < 128 ? [(byte)body.Length] : body.Length < 256 ? [0x81, (byte)body.Length] : [0x82, (byte)(body.Length >> 8), (byte)body.Length];
        return Join([tag], length, body);
    }

    public static byte[] Seq(params byte[][] parts) => Tlv(0x30, parts);
    public static byte[] Octets(byte[] bytes) => Tlv(0x04, bytes);
    public static byte[] Integer(int value) => Tlv(0x02, value < 128 ? [(byte)value] : value < 32768 ? [(byte)(value >> 8), (byte)value] : [0, (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public static byte[] Oid(string dotted)
    {
        var parts = dotted.Split('.').Select(long.Parse).ToArray();
        var bytes = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        foreach (var part in parts.Skip(2))
        {
            var chunk = new List<byte> { (byte)(part & 0x7f) };
            for (var rest = part >> 7; rest > 0; rest >>= 7) chunk.Insert(0, (byte)(0x80 | (rest & 0x7f)));
            bytes.AddRange(chunk);
        }
        return Tlv(0x06, bytes.ToArray());
    }

    /// A key4.db entry as current Firefox writes one: PBES2, PBKDF2-HMAC-SHA256 over SHA-256(salt + password), then
    /// AES-256-CBC with the IV's last fourteen bytes kept.
    public static byte[] Pbes2(byte[] plain, byte[] globalSalt, string password = "", int rounds = 1000)
    {
        var entrySalt = RandomNumberGenerator.GetBytes(32);
        var key = Rfc2898DeriveBytes.Pbkdf2(SHA256.HashData([.. globalSalt, .. Encoding.UTF8.GetBytes(password)]), entrySalt, rounds, HashAlgorithmName.SHA256, 32);
        var tail = RandomNumberGenerator.GetBytes(14);
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] iv = [0x04, 0x0e, .. tail];
        var sealedText = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        return Seq(Seq(Oid("1.2.840.113549.1.5.13"), Seq(Seq(Oid("1.2.840.113549.1.5.12"), Seq(Octets(entrySalt), Integer(rounds), Integer(32), Seq(Oid("1.2.840.113549.2.9")))), Seq(Oid("2.16.840.1.101.3.4.1.42"), Octets(tail)))), Octets(sealedText));
    }

    /// One logins.json field: base64 DER of the key's id, AES-256-CBC with its IV, and the ciphertext.
    public static string LoginField(string text, byte[] master)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = master;
        var sealedText = aes.EncryptCbc(Encoding.UTF8.GetBytes(text), iv, PaddingMode.PKCS7);
        return Convert.ToBase64String(Seq(Octets(new byte[16]), Seq(Oid("2.16.840.1.101.3.4.1.42"), Octets(iv)), Octets(sealedText)));
    }

    /// A Chromium v10 password: the mark, a 12-byte nonce, AES-256-GCM, the tag.
    public static byte[] V10(string password, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(password);
        var sealedText = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, sealedText, tag);
        return Join("v10"u8.ToArray(), nonce, sealedText, tag);
    }
}
