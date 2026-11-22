using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Search.Core;

// Chrome extensions, straight from the Chrome Web Store (Crx.swift).
//
// An extension there is a .crx: a zip with a signed header in front of it. It is fetched from the same public update
// address every Chromium browser asks, the header is checked, and the zip is unpacked into Search's own folder for
// WebView2 to load.
//
// The check that matters: a Chrome extension's id is not a name anybody chose. It is the first sixteen bytes of the
// SHA-256 of its public key, written as letters a to p. So the header must carry a public key that hashes to the id
// that was asked for, and a signature by that key over the zip that came with it. A file that was altered on the way,
// or an extension passed off under another's id, fails one or the other.
//
// WebView2 loads only unpacked folders and gives one an id from the "key" in its manifest, so the key that held up
// here is handed back with the zip: written into the unpacked copy's manifest, it keeps the store's id.

/// Why an extension wasn't taken (Crx.Refused in the Swift).
public enum CrxRefusal { NotAnId, Download, Empty, NotCrx, UnsignedOrWrong, Unpack }

public sealed class CrxRefused(CrxRefusal kind, int code = 0, Exception? inner = null) : Exception(Words(kind, code), inner)
{
    public CrxRefusal Kind { get; } = kind;
    /// The HTTP status, for Download.
    public int Code { get; } = code;

    private static string Words(CrxRefusal kind, int code) => kind switch
    {
        CrxRefusal.NotAnId => "That isn't a Chrome Web Store link or extension id",
        CrxRefusal.Download => $"The Chrome Web Store answered {code}",
        CrxRefusal.Empty => "The Chrome Web Store has nothing for that id — it may have been taken down, or only exist for old versions of Chrome",
        CrxRefusal.NotCrx => "What came back isn't a Chrome extension",
        CrxRefusal.UnsignedOrWrong => "The extension's signature doesn't hold up",
        _ => "The extension couldn't be unpacked",
    };
}

/// The zip inside a CRX3 whose signature held, and the public key (SubjectPublicKeyInfo DER) that both signed it and
/// hashes to its id.
public sealed record VerifiedCrx(byte[] Zip, byte[] PublicKey);

public static partial class Crx
{
    /// The version of Chrome the store is told it is talking to, and the one extensions see. Some set a minimum; the
    /// store refuses to hand those to a browser that says it is older, and some turn a feature off below a version
    /// (1Password opens its popup to unlock only from Chrome 149). Kept at Chrome's stable version; look again every
    /// few releases.
    public const string ChromeVersion = "156.0.0.0";

    /// Where every Chromium browser asks for extensions. Overridable per call, so a test run can ask a local server.
    public const string Endpoint = "https://clients2.google.com/service/update2/crx";

    [GeneratedRegex("(?<![a-z])([a-p]{32})(?![a-z])")] private static partial Regex IdExpression();
    [GeneratedRegex("^/detail/(?:[^/]+/)?([a-p]{32})(?:/.*)?$")] private static partial Regex StoreDetailExpression();
    [GeneratedRegex("^/webstore/detail/(?:[^/]+/)?([a-p]{32})(?:/.*)?$")] private static partial Regex WebstoreDetailExpression();
    [GeneratedRegex(@"<updatecheck\b[^>]*>")] private static partial Regex UpdateCheckExpression();
    [GeneratedRegex(@"\bstatus\s*=\s*[""']ok[""']")] private static partial Regex StatusOkExpression();
    [GeneratedRegex(@"\bversion\s*=\s*[""']([^""']+)[""']")] private static partial Regex VersionExpression();

    /// Thirty-two letters from a to p, wherever they are: a bare id, a store link, an old chrome.google.com/webstore link.
    public static string? Id(string text) => IdExpression().Match(text.ToLowerInvariant()) is { Success: true } m ? m.Groups[1].Value : null;

    /// The extension a store page is about: the id in its /detail/ path and nowhere else, since the listing the page
    /// shows is that one. Only on the store itself, over https, with nothing before its host.
    public static string? StoreId(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0) return null;
        var expression = url.Host.ToLowerInvariant() switch { "chromewebstore.google.com" => StoreDetailExpression(), "chrome.google.com" => WebstoreDetailExpression(), _ => null };
        return expression?.Match(Uri.UnescapeDataString(url.AbsolutePath)) is { Success: true } m ? m.Groups[1].Value : null;
    }

    public static Uri DownloadUrl(string id, string? endpoint = null) =>
        Query(endpoint, ("response", "redirect"), ("prodversion", ChromeVersion), ("acceptformat", "crx3"), ("x", $"id={id}&installsource=ondemand&uc"));

    /// Asks whether there is a version newer than `version`; the answer is read by NewerVersion.
    public static Uri UpdateCheckUrl(string id, string version, string? endpoint = null) =>
        Query(endpoint, ("response", "updatecheck"), ("prodversion", ChromeVersion), ("acceptformat", "crx3"), ("x", $"id={id}&v={version}&uc"));

    /// Each value escaped whole: "x" carries its own query, whose = and & are the value's, not the address's.
    private static Uri Query(string? endpoint, params (string Name, string Value)[] items)
    {
        var start = endpoint ?? Endpoint;
        return new Uri(start + (start.Contains('?') ? "&" : "?") + string.Join("&", items.Select(i => $"{Uri.EscapeDataString(i.Name)}={Uri.EscapeDataString(i.Value)}")));
    }

    /// The version an update check offers, when it is one other than `current`. The answer is the <updatecheck>
    /// element alone: status="ok" with a version when there is a newer one, "noupdate" when not. Read across the whole
    /// reply, the first version="" is the XML declaration's "1.0", and status="ok" is on <app> either way, which took
    /// every reply for an update.
    public static string? NewerVersion(string updateCheckXml, string current)
    {
        if (UpdateCheckExpression().Match(updateCheckXml) is not { Success: true } check || !StatusOkExpression().IsMatch(check.Value)) return null;
        return VersionExpression().Match(check.Value) is { Success: true } v && v.Groups[1].Value != current ? v.Groups[1].Value : null;
    }

    /// The id's letters: each half-byte is a letter from a (0) to p (15).
    public static string Letters(ReadOnlySpan<byte> bytes)
    {
        var text = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++) { text[2 * i] = (char)('a' + (bytes[i] >> 4)); text[2 * i + 1] = (char)('a' + (bytes[i] & 0x0f)); }
        return new string(text);
    }

    /// The id a public key (SubjectPublicKeyInfo DER) gives an extension, in Chrome and in WebView2 alike.
    public static string IdOfKey(byte[] spki) => Letters(SHA256.HashData(spki).AsSpan(0, 16));

    // Checking.

    private const int ProofRsa = 2, ProofEcdsa = 3, SignedHeader = 10000, CrxIdField = 1, ProofKey = 1, ProofSignature = 2;
    private static readonly byte[] Magic = "Cr24"u8.ToArray();
    private static readonly byte[] SignedPrefix = "CRX3 SignedData\0"u8.ToArray();

    /// The zip inside a CRX3, once its signature has been checked against `id`.
    public static VerifiedCrx Verify(byte[] crx, string id)
    {
        if (Id(id) != id) throw new CrxRefused(CrxRefusal.NotAnId);
        if (crx.Length <= 12 || !crx.AsSpan(0, 4).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(4)) != 3) throw new CrxRefused(CrxRefusal.NotCrx);
        var headerSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(8));
        if (12 + headerSize > crx.Length) throw new CrxRefused(CrxRefusal.NotCrx);
        var header = crx.AsSpan(12, (int)headerSize).ToArray();
        var zip = crx.AsSpan(12 + (int)headerSize).ToArray();

        // CrxFileHeader: 2 = sha256_with_rsa proofs, 3 = sha256_with_ecdsa proofs, 10000 = signed_header_data
        // (SignedData: 1 = crx_id).
        var fields = Protobuf(header);
        if (fields.FirstOrDefault(f => f.Field == SignedHeader).Bytes is not { } signed) throw new CrxRefused(CrxRefusal.UnsignedOrWrong);
        if (Protobuf(signed).FirstOrDefault(f => f.Field == CrxIdField).Bytes is not { } crxId || Letters(crxId) != id) throw new CrxRefused(CrxRefusal.UnsignedOrWrong);
        var message = Message(signed, zip);

        // A proof whose key is the one the id is made from, and whose signature holds over the message. The store
        // adds a proof of its own; that one is fine to hold too but is not the one that counts.
        foreach (var (field, bytes) in fields)
        {
            if (field is not (ProofRsa or ProofEcdsa)) continue;
            var proof = Protobuf(bytes);
            if (proof.FirstOrDefault(p => p.Field == ProofKey).Bytes is not { } key || proof.FirstOrDefault(p => p.Field == ProofSignature).Bytes is not { } signature || IdOfKey(key) != id) continue;
            if (field == ProofRsa ? VerifyRsa(key, signature, message) : VerifyEcdsa(key, signature, message)) return new VerifiedCrx(zip, key);
        }
        throw new CrxRefused(CrxRefusal.UnsignedOrWrong);
    }

    /// What is signed: a fixed prefix, the signed header's length and the signed header, then the zip.
    private static byte[] Message(byte[] signed, byte[] zip)
    {
        var message = new byte[SignedPrefix.Length + 4 + signed.Length + zip.Length];
        SignedPrefix.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(SignedPrefix.Length), (uint)signed.Length);
        signed.CopyTo(message, SignedPrefix.Length + 4);
        zip.CopyTo(message, SignedPrefix.Length + 4 + signed.Length);
        return message;
    }

    private static bool VerifyRsa(byte[] spki, byte[] signature, byte[] message)
    {
        try { using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(spki, out _); return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
        catch (CryptographicException) { return false; }
    }

    /// P-256 only, as Chrome takes; its signature is DER, as Chrome writes it.
    private static bool VerifyEcdsa(byte[] spki, byte[] signature, byte[] message)
    {
        try { using var ec = ECDsa.Create(); ec.ImportSubjectPublicKeyInfo(spki, out _); return ec.KeySize == 256 && ec.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { return false; }
    }

    /// Length-delimited fields only, which is all a CRX header holds; anything else is stepped over.
    internal static List<(int Field, byte[] Bytes)> Protobuf(byte[] b)
    {
        var output = new List<(int, byte[])>();
        var i = 0;
        long? Varint()
        {
            long value = 0;
            for (var shift = 0; i < b.Length && shift <= 56; shift += 7)
            {
                var one = b[i++];
                value |= (long)(one & 0x7f) << shift;
                if ((one & 0x80) == 0) return value;
            }
            return null;
        }
        while (i < b.Length)
        {
            if (Varint() is not { } key) break;
            switch (key & 7)
            {
                case 2:
                    if (Varint() is not { } length || length < 0 || i + length > b.Length) return output;
                    output.Add(((int)(key >> 3), b.AsSpan(i, (int)length).ToArray()));
                    i += (int)length;
                    break;
                case 0: Varint(); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                default: return output;
            }
        }
        return output;
    }

    // Packing: the inverse, so tests and a test tool can make store packages without the network.

    /// A CRX3 of `zip` signed by `key`, whose id is the key's.
    public static byte[] Pack(byte[] zip, RSA key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        var signed = SignedHeaderFor(spki);
        return Packed(zip, signed, (ProofRsa, spki, key.SignData(Message(signed, zip), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    /// The same with a P-256 key, as a sha256_with_ecdsa proof.
    public static byte[] Pack(byte[] zip, ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        var signed = SignedHeaderFor(spki);
        return Packed(zip, signed, (ProofEcdsa, spki, key.SignData(Message(signed, zip), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    internal static byte[] SignedHeaderFor(byte[] spki) => Field(CrxIdField, SHA256.HashData(spki).AsSpan(0, 16).ToArray());

    /// A CRX3 with these proofs over whatever signed header it is given: tests make broken ones with it.
    internal static byte[] Packed(byte[] zip, byte[] signedHeader, params (int Field, byte[] Key, byte[] Signature)[] proofs)
    {
        var header = proofs.SelectMany(p => Field(p.Field, [.. Field(ProofKey, p.Key), .. Field(ProofSignature, p.Signature)])).Concat(Field(SignedHeader, signedHeader)).ToArray();
        var crx = new byte[12 + header.Length + zip.Length];
        Magic.CopyTo(crx, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(crx.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(crx.AsSpan(8), (uint)header.Length);
        header.CopyTo(crx, 12);
        zip.CopyTo(crx, 12 + header.Length);
        return crx;
    }

    internal static byte[] Field(int field, byte[] bytes) => [.. Varint(((ulong)field << 3) | 2), .. Varint((ulong)bytes.Length), .. bytes];

    private static byte[] Varint(ulong value)
    {
        var output = new List<byte>();
        do { var one = (byte)(value & 0x7f); value >>= 7; output.Add(value == 0 ? one : (byte)(one | 0x80)); } while (value != 0);
        return [.. output];
    }

    // Unpacking.

    private const int UnixLink = 0xA000, UnixType = 0xF000, ReparsePoint = 0x400;

    /// Unpacks a zip into `folder`, which is replaced whole. Into a scratch folder beside it first, so a refused or
    /// broken package leaves the one that was there alone, and so the scratch folder is moved into place on the same
    /// drive. Refused: an entry that would land outside the folder (an absolute path, a drive, ".." climbing out), and
    /// any link at all, since the pages a package ships are rewritten in place and a link among them would have that
    /// rewrite land wherever it points. Nothing an extension needs is a link. And manifest.json must be at the root,
    /// as Chrome requires: one inside a single top-level folder is not taken either.
    public static void Unpack(byte[] zip, string folder)
    {
        var target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(target) ?? throw new CrxRefused(CrxRefusal.Unpack);
        var scratch = Path.Combine(parent, $".unpacking-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(scratch);
            Extract(zip, scratch);
            if (!File.Exists(Path.Combine(scratch, "manifest.json"))) throw new CrxRefused(CrxRefusal.Unpack);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(scratch, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { throw new CrxRefused(CrxRefusal.Unpack, inner: e); }
        finally { if (Directory.Exists(scratch)) try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static void Extract(byte[] zip, string into)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var root = into + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var unix = (entry.ExternalAttributes >> 16) & UnixType;
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || unix == UnixLink || (entry.ExternalAttributes & ReparsePoint) != 0) throw new CrxRefused(CrxRefusal.Unpack);
            var path = Path.GetFullPath(Path.Combine(into, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.TrimEnd(Path.DirectorySeparatorChar) != into) throw new CrxRefused(CrxRefusal.Unpack);
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

}
