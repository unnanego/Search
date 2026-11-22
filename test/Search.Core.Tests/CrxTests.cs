using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// Zips and folders made for the extension tests.
internal static class Scratch
{
    /// A folder of its own under the temp folder, made empty.
    public static string Folder(string stem = "ext")
    {
        var folder = Path.Combine(Path.GetTempPath(), $"search-{stem}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static byte[] Zip(params (string Name, string Text)[] files) => Zip(files.Select(f => (f.Name, f.Text, 0)).ToArray());

    /// Each entry with the Unix mode it carries in its external attributes, for links.
    public static byte[] Zip(params (string Name, string Text, int UnixMode)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text, mode) in files)
            {
                var entry = zip.CreateEntry(name);
                if (mode != 0) entry.ExternalAttributes = mode << 16;
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        return buffer.ToArray();
    }

    public static byte[] Extension => Zip(("manifest.json", """{"manifest_version":3,"name":"Test","version":"1.0"}"""), ("js/worker.js", "self.x = 1;"));
}

public class CrxTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RSA Other = RSA.Create(2048);
    private static string IdOf(RSA key) => Crx.IdOfKey(key.ExportSubjectPublicKeyInfo());
    private static readonly string Id = IdOf(Key);

    private static CrxRefusal Refusal(Action act) => Assert.Throws<CrxRefused>(act).Kind;

    // Ids.

    [Fact] public void ABareIdIsAnId() => Assert.Equal("nkbihfbeogaeaoehlefnkodbefgpgknn", Crx.Id("nkbihfbeogaeaoehlefnkodbefgpgknn"));
    [Fact] public void AnIdIsFoundInALinkAndLowercased() => Assert.Equal("nkbihfbeogaeaoehlefnkodbefgpgknn", Crx.Id("https://chromewebstore.google.com/detail/metamask/NKBIHFBEOGAEAOEHLEFNKODBEFGPGKNN?hl=en"));
    [Fact] public void ThirtyThreeLettersAreNoId() => Assert.Null(Crx.Id("nkbihfbeogaeaoehlefnkodbefgpgknna"));
    [Fact] public void LettersPastPAreNoId() => Assert.Null(Crx.Id("zkbihfbeogaeaoehlefnkodbefgpgknn"));
    [Fact] public void NothingIsNoId() => Assert.Null(Crx.Id("hello"));

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("https://chromewebstore.google.com/detail/nkbihfbeogaeaoehlefnkodbefgpgknn", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("https://chromewebstore.google.com/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn/reviews", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("https://CHROMEWEBSTORE.google.com/detail/x/nkbihfbeogaeaoehlefnkodbefgpgknn?hl=de", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("https://chrome.google.com/webstore/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("https://chromewebstore.google.com/detail/caf%C3%A9/nkbihfbeogaeaoehlefnkodbefgpgknn", "nkbihfbeogaeaoehlefnkodbefgpgknn")]
    [InlineData("http://chromewebstore.google.com/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://me@chromewebstore.google.com/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://chromewebstore.google.com.evil/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://chromewebstore.google.com/webstore/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://chrome.google.com/detail/metamask/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://chromewebstore.google.com/search?q=nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    [InlineData("https://chromewebstore.google.com/detail/a/b/nkbihfbeogaeaoehlefnkodbefgpgknn", null)]
    public void TheStoreIdIsOnlyInADetailPathOnTheStore(string url, string? id) => Assert.Equal(id, Crx.StoreId(new Uri(url)));

    [Fact] public void ARelativeAddressHasNoStoreId() => Assert.Null(Crx.StoreId(new Uri("/detail/x/nkbihfbeogaeaoehlefnkodbefgpgknn", UriKind.Relative)));

    [Fact] public void EachHalfByteIsALetter() => Assert.Equal("aappbk", Crx.Letters([0x00, 0xff, 0x1a]));

    [Fact]
    public void AKeysIdIsItsHashsFirstSixteenBytesInLetters()
    {
        var spki = Key.ExportSubjectPublicKeyInfo();
        var id = Crx.IdOfKey(spki);
        Assert.Equal(Crx.Letters(SHA256.HashData(spki)[..16]), id);
        Assert.Equal(id, Crx.Id(id));
    }

    // Addresses.

    [Fact]
    public void TheDownloadAddressCarriesItsQueryAsOneValue() => Assert.Equal(
        "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=156.0.0.0&acceptformat=crx3&x=id%3Dnkbihfbeogaeaoehlefnkodbefgpgknn%26installsource%3Dondemand%26uc",
        Crx.DownloadUrl("nkbihfbeogaeaoehlefnkodbefgpgknn").AbsoluteUri);

    [Fact]
    public void TheUpdateCheckAddressNamesTheVersion() => Assert.Equal(
        "https://clients2.google.com/service/update2/crx?response=updatecheck&prodversion=156.0.0.0&acceptformat=crx3&x=id%3Dnkbihfbeogaeaoehlefnkodbefgpgknn%26v%3D1.2.3%26uc",
        Crx.UpdateCheckUrl("nkbihfbeogaeaoehlefnkodbefgpgknn", "1.2.3").AbsoluteUri);

    [Fact]
    public void ATestRunCanAskALocalServer()
    {
        Assert.Equal("http://127.0.0.1:8123/crx?response=redirect&prodversion=156.0.0.0&acceptformat=crx3&x=id%3Dabc%26installsource%3Dondemand%26uc", Crx.DownloadUrl("abc", "http://127.0.0.1:8123/crx").AbsoluteUri);
        Assert.StartsWith("http://localhost/u?k=1&response=updatecheck&", Crx.UpdateCheckUrl("abc", "1", "http://localhost/u?k=1").AbsoluteUri);
    }

    [Fact] public void TheChromeVersionIsStable() => Assert.Equal("156.0.0.0", Crx.ChromeVersion);

    // Update checks.

    private const string Update = """<?xml version="1.0" encoding="UTF-8"?><gupdate xmlns="http://www.google.com/update2/response" protocol="2.0" server="prod"><daystart elapsed_days="6898" elapsed_seconds="100"/><app appid="nkbihfbeogaeaoehlefnkodbefgpgknn" cohort="1::" cohortname="" status="ok"><updatecheck _esbAllowlist="true" codebase="https://clients2.googleusercontent.com/crx/blobs/x/ext.crx" fp="1.abc" hash_sha256="00ff" protected="0" size="12345" status="ok" version="12.4.1"/></app></gupdate>""";
    private const string NoUpdate = """<?xml version="1.0" encoding="UTF-8"?><gupdate xmlns="http://www.google.com/update2/response" protocol="2.0" server="prod"><daystart elapsed_days="6898" elapsed_seconds="100"/><app appid="nkbihfbeogaeaoehlefnkodbefgpgknn" cohort="1::" cohortname="" status="ok"><updatecheck status="noupdate"/></app></gupdate>""";

    [Fact] public void ANewerVersionIsOffered() => Assert.Equal("12.4.1", Crx.NewerVersion(Update, "12.3.0"));
    [Fact] public void TheSameVersionIsNoUpdate() => Assert.Null(Crx.NewerVersion(Update, "12.4.1"));
    [Fact] public void NoUpdateIsntMistakenForOneByTheDeclarationsVersionOrTheAppsStatus() => Assert.Null(Crx.NewerVersion(NoUpdate, "12.3.0"));
    [Fact] public void AReplyWithoutAnUpdateCheckIsNoUpdate() => Assert.Null(Crx.NewerVersion("""<?xml version="1.0"?><gupdate><app status="ok" version="9"/></gupdate>""", "1"));
    [Fact] public void GarbageIsNoUpdate() => Assert.Null(Crx.NewerVersion("<html>502</html>", "1"));
    [Fact] public void SingleQuotesRead() => Assert.Equal("2.0", Crx.NewerVersion("<updatecheck status='ok' version='2.0'/>", "1.0"));

    // Checking.

    [Fact]
    public void APackageItsKeySignedVerifies()
    {
        var zip = Scratch.Extension;
        var verified = Crx.Verify(Crx.Pack(zip, Key), Id);
        Assert.Equal(zip, verified.Zip);
        Assert.Equal(Key.ExportSubjectPublicKeyInfo(), verified.PublicKey);
    }

    [Fact]
    public void AnAlteredZipIsRefused()
    {
        var crx = Crx.Pack(Scratch.Extension, Key);
        crx[^5] ^= 0x01;
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(crx, Id)));
    }

    [Fact]
    public void AnAlteredSignatureIsRefused()
    {
        var zip = Scratch.Extension;
        var spki = Key.ExportSubjectPublicKeyInfo();
        var signed = Crx.SignedHeaderFor(spki);
        var signature = Sign(Key, signed, zip);
        signature[10] ^= 0x01;
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Crx.Packed(zip, signed, (2, spki, signature)), Id)));
    }

    [Fact] public void AnotherIdIsRefused() => Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Crx.Pack(Scratch.Extension, Key), IdOf(Other))));

    [Fact]
    public void AnExtensionPassedOffUnderAnothersIdIsRefused()
    {
        // The header names the other id, and the package is signed, but by a key that isn't the other's.
        var zip = Scratch.Extension;
        var signed = Crx.SignedHeaderFor(Other.ExportSubjectPublicKeyInfo());
        var crx = Crx.Packed(zip, signed, (2, Key.ExportSubjectPublicKeyInfo(), Sign(Key, signed, zip)));
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(crx, IdOf(Other))));
    }

    [Fact]
    public void AHeaderAlteredAfterSigningIsRefused()
    {
        // Signed over one signed header, shipped with another naming the same id plus a field of its own.
        var zip = Scratch.Extension;
        var spki = Key.ExportSubjectPublicKeyInfo();
        var signed = Crx.SignedHeaderFor(spki);
        var crx = Crx.Packed(zip, [.. signed, .. Crx.Field(7, [1, 2, 3])], (2, spki, Sign(Key, signed, zip)));
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(crx, Id)));
    }

    [Fact]
    public void TheStoresOwnProofBesideTheOwnersIsFine()
    {
        var zip = Scratch.Extension;
        var spki = Key.ExportSubjectPublicKeyInfo();
        var signed = Crx.SignedHeaderFor(spki);
        var store = (2, Other.ExportSubjectPublicKeyInfo(), Sign(Other, signed, zip));
        Assert.Equal(zip, Crx.Verify(Crx.Packed(zip, signed, store, (2, spki, Sign(Key, signed, zip))), Id).Zip);
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Crx.Packed(zip, signed, store), Id)));
    }

    [Fact]
    public void AHeaderWithNoSignedDataIsRefused()
    {
        var header = Crx.Field(2, [.. Crx.Field(1, Key.ExportSubjectPublicKeyInfo()), .. Crx.Field(2, [1, 2, 3])]);
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Raw(3, header, Scratch.Extension), Id)));
    }

    [Fact]
    public void AnEcdsaProofVerifies()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Scratch.Extension;
        var spki = ec.ExportSubjectPublicKeyInfo();
        var verified = Crx.Verify(Crx.Pack(zip, ec), Crx.IdOfKey(spki));
        Assert.Equal(zip, verified.Zip);
        Assert.Equal(spki, verified.PublicKey);
    }

    [Fact]
    public void AnAlteredEcdsaProofIsRefused()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var crx = Crx.Pack(Scratch.Extension, ec);
        crx[^3] ^= 0x01;
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(crx, Crx.IdOfKey(ec.ExportSubjectPublicKeyInfo()))));
    }

    [Fact]
    public void AnEcdsaKeyOnAnotherCurveIsRefused()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Crx.Pack(Scratch.Extension, ec), Crx.IdOfKey(ec.ExportSubjectPublicKeyInfo()))));
    }

    [Fact]
    public void AnRsaSignatureFiledAsEcdsaIsRefused()
    {
        var zip = Scratch.Extension;
        var spki = Key.ExportSubjectPublicKeyInfo();
        var signed = Crx.SignedHeaderFor(spki);
        Assert.Equal(CrxRefusal.UnsignedOrWrong, Refusal(() => Crx.Verify(Crx.Packed(zip, signed, (3, spki, Sign(Key, signed, zip))), Id)));
    }

    [Fact] public void AZipIsntACrx() => Assert.Equal(CrxRefusal.NotCrx, Refusal(() => Crx.Verify(Scratch.Extension, Id)));
    [Fact] public void TooShortIsntACrx() => Assert.Equal(CrxRefusal.NotCrx, Refusal(() => Crx.Verify("Cr24"u8.ToArray(), Id)));
    [Fact] public void AVersionTwoHeaderIsRefused() => Assert.Equal(CrxRefusal.NotCrx, Refusal(() => Crx.Verify(Raw(2, [1, 2, 3, 4], Scratch.Extension), Id)));

    [Fact]
    public void AHeaderLongerThanTheFileIsRefused()
    {
        var crx = Crx.Pack(Scratch.Extension, Key);
        BinaryPrimitives.WriteUInt32LittleEndian(crx.AsSpan(8), uint.MaxValue);
        Assert.Equal(CrxRefusal.NotCrx, Refusal(() => Crx.Verify(crx, Id)));
    }

    [Fact] public void WhatIsntAnIdIsRefusedFirst() => Assert.Equal(CrxRefusal.NotAnId, Refusal(() => Crx.Verify(Crx.Pack(Scratch.Extension, Key), "not-an-id")));

    [Fact]
    public void RefusalsSayWhy()
    {
        Assert.Equal("The Chrome Web Store answered 404", new CrxRefused(CrxRefusal.Download, 404).Message);
        Assert.Equal(404, new CrxRefused(CrxRefusal.Download, 404).Code);
        Assert.Equal("The extension's signature doesn't hold up", new CrxRefused(CrxRefusal.UnsignedOrWrong).Message);
        Assert.All(Enum.GetValues<CrxRefusal>(), k => Assert.NotEmpty(new CrxRefused(k).Message));
    }

    [Fact]
    public void FieldsThatArentBytesAreSteppedOver()
    {
        // A varint field (1, 150), a fixed64 and a fixed32 before a bytes field 5.
        byte[] header = [0x08, 0x96, 0x01, 0x11, 1, 2, 3, 4, 5, 6, 7, 8, 0x1d, 1, 2, 3, 4, .. Crx.Field(5, [9])];
        var field = Assert.Single(Crx.Protobuf(header));
        Assert.Equal(5, field.Field);
        Assert.Equal(new byte[] { 9 }, field.Bytes);
    }

    private static byte[] Sign(RSA key, byte[] signed, byte[] zip)
    {
        byte[] message = [.. "CRX3 SignedData\0"u8, .. BitConverter.GetBytes((uint)signed.Length), .. signed, .. zip];
        return key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static byte[] Raw(uint version, byte[] header, byte[] zip)
    {
        var crx = new byte[12 + header.Length + zip.Length];
        "Cr24"u8.CopyTo(crx);
        BinaryPrimitives.WriteUInt32LittleEndian(crx.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(crx.AsSpan(8), (uint)header.Length);
        header.CopyTo(crx, 12);
        zip.CopyTo(crx, 12 + header.Length);
        return crx;
    }

    // Unpacking.

    [Fact]
    public void APackageUnpacksInPlaceOfWhatWasThere()
    {
        var root = Scratch.Folder();
        var target = Path.Combine(root, Id);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.js"), "old");
        Crx.Unpack(Scratch.Extension, target);
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.Equal("self.x = 1;", File.ReadAllText(Path.Combine(target, "js", "worker.js")));
        Assert.False(File.Exists(Path.Combine(target, "old.js")));
        Assert.Single(Directory.GetDirectories(root));
    }

    [Fact]
    public void BackslashesAreFolders()
    {
        var target = Path.Combine(Scratch.Folder(), "x");
        Crx.Unpack(Scratch.Zip(("manifest.json", "{}"), ("sub\\file.js", "1")), target);
        Assert.True(File.Exists(Path.Combine(target, "sub", "file.js")));
    }

    [Fact]
    public void ClimbingAndComingBackIsFine()
    {
        var target = Path.Combine(Scratch.Folder(), "x");
        Crx.Unpack(Scratch.Zip(("a/../manifest.json", "{}")), target);
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Theory]
    [InlineData("../evil.js")]
    [InlineData("a/../../evil.js")]
    [InlineData("..\\evil.js")]
    [InlineData("/evil.js")]
    [InlineData("\\evil.js")]
    [InlineData("C:/evil.js")]
    [InlineData("C:evil.js")]
    [InlineData("manifest.json:hidden")]
    public void AnEntryOutsideTheFolderIsRefusedAndNothingIsTouched(string name)
    {
        var root = Scratch.Folder();
        var target = Path.Combine(root, "x");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "manifest.json"), "kept");
        Assert.Equal(CrxRefusal.Unpack, Refusal(() => Crx.Unpack(Scratch.Zip(("manifest.json", "{}"), (name, "pwned")), target)));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(root, "evil.js")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void ALinkIsRefused()
    {
        var target = Path.Combine(Scratch.Folder(), "x");
        var zip = Scratch.Zip(("manifest.json", "{}", 0x81A4), ("link.js", "/etc/passwd", 0xA1FF));
        Assert.Equal(CrxRefusal.Unpack, Refusal(() => Crx.Unpack(zip, target)));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void AManifestOnlyInsideAFolderIsRefused()
    {
        var target = Path.Combine(Scratch.Folder(), "x");
        Assert.Equal(CrxRefusal.Unpack, Refusal(() => Crx.Unpack(Scratch.Zip(("ext/manifest.json", "{}"), ("ext/worker.js", "")), target)));
        Assert.False(Directory.Exists(target));
    }

    [Fact] public void NotAZipIsRefused() => Assert.Equal(CrxRefusal.Unpack, Refusal(() => Crx.Unpack([1, 2, 3, 4, 5], Path.Combine(Scratch.Folder(), "x"))));

    [Fact]
    public void FromTheStoreToAFolderThatKeepsItsId()
    {
        // What the app does with a store package: check it, unpack it, and write the key that held into the copy's
        // manifest, so WebView2 gives the copy the store's id.
        var folder = Path.Combine(Scratch.Folder(), Id);
        var verified = Crx.Verify(Crx.Pack(Scratch.Extension, Key), Id);
        Crx.Unpack(verified.Zip, folder);
        var original = Manifest.ReadObject(Path.Combine(folder, "manifest.json"))!;
        ExtensionPrep.Write(folder, original, Convert.ToBase64String(verified.PublicKey), new SiteAccess(), "// shim", new Dictionary<string, string>());
        var written = Manifest.ReadObject(Path.Combine(folder, "manifest.json"))!;
        Assert.Equal(Id, ExtensionPrep.IdOfKeyBase64(Manifest.Text(written["key"])!));
    }
}
