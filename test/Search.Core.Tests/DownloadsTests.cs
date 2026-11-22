using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class DownloadsTests
{
    public DownloadsTests() => TempStore.Ready();

    private static DateTimeOffset Mac(double seconds) => SwiftJson.Date(seconds)!.Value;

    /// As a Mac's JSONEncoder writes it: newest first, slashes escaped, one from before addresses were noted.
    private const string MacFile = """
        [{"name":"report.pdf","from":"files.example.com","path":"\/Users\/me\/Downloads\/report.pdf","date":785000100.25,"address":"https:\/\/files.example.com\/report.pdf?sig=abc"},
         {"name":"old.zip","from":"","path":"\/Users\/me\/Downloads\/old.zip","date":785000000}]
        """;

    private static (Downloads Downloads, string File) Fresh(string? contents = null)
    {
        var file = TempStore.Unique("downloads");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Downloads(file), file);
    }

    private static async Task<List<Keep>> Written(string file)
    {
        await Disk.Flush();
        return Downloads.Decode(await File.ReadAllBytesAsync(Store.File(file))) ?? throw new Xunit.Sdk.XunitException("didn't decode");
    }

    private static Keep K(string path, string? address = null) => new(Path.GetFileName(path), "example.com", path, Mac(785000000)) { Address = address };

    [Fact]
    public void AMacFileReads()
    {
        var (downloads, _) = Fresh(MacFile);
        Assert.Equal(2, downloads.Kept.Count);
        Assert.Equal(new Keep("report.pdf", "files.example.com", "/Users/me/Downloads/report.pdf", Mac(785000100.25)) { Address = "https://files.example.com/report.pdf?sig=abc" }, downloads.Kept[0]);
        Assert.Null(downloads.Kept[1].Address);
        Assert.Equal("/Users/me/Downloads/old.zip", downloads.Kept[1].Id);
        Assert.False(downloads.Kept[1].StillThere);
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        Assert.Equal(
            """[{"name":"a.zip","from":"example.com","path":"C:\\Users\\me\\Downloads\\a.zip","date":785000000,"address":"https:\/\/example.com\/a.zip"},{"name":"b.zip","from":"example.com","path":"\/b.zip","date":785000000}]""",
            Encoding.UTF8.GetString(Downloads.Encode([K(@"C:\Users\me\Downloads\a.zip", "https://example.com/a.zip"), K("/b.zip")])));
        var back = Downloads.Decode(Encoding.UTF8.GetBytes(MacFile))!;
        Assert.Equal(back, Downloads.Decode(Downloads.Encode(back)));
    }

    [Theory]
    [InlineData("""[{"name":"a","from":"","path":"p"}]""")]
    [InlineData("""[{"name":"a","path":"p","date":1}]""")]
    [InlineData("""[{"name":"a","from":"","path":"p","date":1,"address":7}]""")]
    [InlineData("""{"name":"a"}""")]
    public void WhatSwiftWontDecodeDoesNot(string json) => Assert.Null(Downloads.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task ACorruptFileIsSetAside()
    {
        var (downloads, file) = Fresh("""[{"name":"a"}]""");
        Assert.Empty(downloads.Kept);
        Assert.Single(Directory.GetFiles(Store.Folder, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-*.json"));
        downloads.Add(K(@"C:\x.zip"));
        Assert.Single(await Written(file));
    }

    [Fact]
    public async Task TheNewestFifty()
    {
        var (downloads, file) = Fresh();
        var heard = 0;
        ((INotifyPropertyChanged)downloads).PropertyChanged += (_, _) => heard++;
        for (var i = 0; i < 60; i++) downloads.Add(K($@"C:\d\{i}.zip"));
        Assert.Equal(Downloads.Most, downloads.Kept.Count);
        Assert.Equal(@"C:\d\59.zip", downloads.Kept[0].Path);
        Assert.Equal(@"C:\d\10.zip", downloads.Kept[^1].Path);
        Assert.Equal(60, heard);

        // The same path again comes to the front, once.
        downloads.Add(K(@"C:\d\30.zip", "https://example.com/30"));
        Assert.Equal(Downloads.Most, downloads.Kept.Count);
        Assert.Equal("https://example.com/30", downloads.Kept[0].Address);
        Assert.Single(downloads.Kept, k => k.Path == @"C:\d\30.zip");

        downloads.Forget(downloads.Kept[0]);
        Assert.DoesNotContain(downloads.Kept, k => k.Path == @"C:\d\30.zip");
        Assert.Equal(49, (await Written(file)).Count);
        downloads.ForgetAll();
        Assert.Empty(await Written(file));
    }

    [Fact]
    public async Task ForgettingAddresses()
    {
        var (downloads, file) = Fresh(MacFile);
        downloads.ForgetAddresses();
        Assert.All(downloads.Kept, k => Assert.Null(k.Address));
        Assert.Equal(2, downloads.Kept.Count);
        Assert.All(await Written(file), k => Assert.Null(k.Address));
        var heard = 0;
        ((INotifyPropertyChanged)downloads).PropertyChanged += (_, _) => heard++;
        downloads.ForgetAddresses();
        Assert.Equal(0, heard);
    }

    [Fact]
    public void WhatARecordKeeps()
    {
        Assert.Equal("https://example.com/f.zip?sig=1#x", Keep.AddressOf(new Uri("https://me:secret@example.com/f.zip?sig=1#x")));
        Assert.Null(Keep.AddressOf(new Uri("blob:https://example.com/1234")));
        Assert.Null(Keep.AddressOf(new Uri("data:text/plain,hello")));
        Assert.Null(Keep.AddressOf(null));

        var saved = Keep.Saved(@"C:\Users\me\Downloads\f.zip", new Uri("https://cdn.example.com/f.zip"));
        Assert.Equal(("f.zip", "cdn.example.com", @"C:\Users\me\Downloads\f.zip", "https://cdn.example.com/f.zip"), (saved.Name, saved.From, saved.Path, saved.Address));
        Assert.True(DateTimeOffset.UtcNow - saved.Date < TimeSpan.FromMinutes(1));
        Assert.Null(Keep.Saved(@"C:\f.zip", new Uri("https://cdn.example.com/f.zip"), keepsAddress: false).Address);
        Assert.Equal("", Keep.Saved(@"C:\f.zip", null).From);
    }

    [Fact]
    public void AFreeName()
    {
        var folder = Path.Combine(Store.Folder, $"free-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        Assert.Equal(Path.Combine(folder, "report.pdf"), Downloads.Free("report.pdf", folder));
        File.WriteAllText(Path.Combine(folder, "report.pdf"), "");
        File.WriteAllText(Path.Combine(folder, "report 2.pdf"), "");
        Assert.Equal(Path.Combine(folder, "report 3.pdf"), Downloads.Free("report.pdf", folder));
        File.WriteAllText(Path.Combine(folder, "README"), "");
        Assert.Equal(Path.Combine(folder, "README 2"), Downloads.Free("README", folder));
        File.WriteAllText(Path.Combine(folder, "a.tar.gz"), "");
        Assert.Equal(Path.Combine(folder, "a.tar 2.gz"), Downloads.Free("a.tar.gz", folder));
    }

    [Fact]
    public void WhatCanBeAskedForAgain()
    {
        Assert.True(Downloads.Replayable(null, false));
        Assert.True(Downloads.Replayable("head", false));
        Assert.False(Downloads.Replayable("POST", false));
        Assert.False(Downloads.Replayable("GET", true));
    }

    /// A folder a download can't be written into is found out before WebView2 fails on it (#1291), and nothing is
    /// left behind.
    [Fact]
    public void WhereADownloadCanGo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"can-write-{Guid.NewGuid():N}");
        try
        {
            var missing = Path.Combine(root, "open", "deeper");
            Assert.True(Downloads.CanWrite(missing), "a missing folder is made");
            Assert.Empty(Directory.EnumerateFileSystemEntries(missing));
            var file = Path.Combine(root, "a file");
            File.WriteAllText(file, "x");
            Assert.False(Downloads.CanWrite(Path.Combine(file, "inside")), "a folder under a file can't be made");
            Assert.False(Downloads.CanWrite(null));
            Assert.False(Downloads.CanWrite(""));
            if (!OperatingSystem.IsWindows()) return;
            var locked = Path.Combine(root, "locked");
            var info = Directory.CreateDirectory(locked);
            var rules = info.GetAccessControl();
            rules.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(rules);
            Assert.False(Downloads.CanWrite(locked), "a folder you may not make files in");
            rules.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(rules);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
