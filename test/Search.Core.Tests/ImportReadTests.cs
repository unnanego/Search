using System.Diagnostics;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class SqliteTests
{
    [Fact]
    public void WritesAndReadsBackEveryKindOfColumn()
    {
        using var at = new ImportFixture();
        var file = ImportFixture.Db(at.Path("t.sqlite"), "CREATE TABLE t (n INTEGER, s TEXT, b BLOB, r REAL)");
        ImportFixture.Run(file, "INSERT INTO t VALUES (?, ?, ?, ?)", 42L, "héllo", new byte[] { 1, 2, 3 }, 1.5);
        using var db = Sqlite.Open(file)!;
        var row = db.Rows("SELECT n, s, b, r, NULL FROM t").Select(r => (r.Long(0), r.Text(1), r.Blob(2), r.Double(3), r.Text(4))).Single();
        Assert.Equal((42L, "héllo", 1.5, (string?)null), (row.Item1, row.Item2, row.Item4, row.Item5));
        Assert.Equal(new byte[] { 1, 2, 3 }, row.Item3);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM t WHERE s = ?", "héllo"));
    }

    [Fact]
    public void AStatementForATableThatIsntThereReadsNothing()
    {
        using var at = new ImportFixture();
        var file = ImportFixture.Db(at.Path("t.sqlite"), "CREATE TABLE t (n INTEGER)");
        using var db = Sqlite.Open(file)!;
        Assert.False(db.Prepares("SELECT x FROM nowhere"));
        Assert.Empty(db.Rows("SELECT x FROM nowhere"));
        Assert.Null(db.Scalar("SELECT COUNT(*) FROM nowhere"));
    }

    [Fact]
    public void ReadOnlyRefusesToWrite()
    {
        using var at = new ImportFixture();
        var file = ImportFixture.Db(at.Path("t.sqlite"), "CREATE TABLE t (n INTEGER)");
        using var db = Sqlite.Open(file)!;
        Assert.Throws<InvalidOperationException>(() => db.Exec("INSERT INTO t VALUES (1)"));
    }
}

public class ImportReadTests
{
    [Fact]
    public void ReadsAFileInsideItsFolder()
    {
        using var at = new ImportFixture();
        var file = at.Write(at.Path("Local", "B", "Bookmarks"), "{}");
        Assert.Equal("{}", Encoding.UTF8.GetString(ImportRead.Data(file, at.Path("Local", "B"))));
    }

    [Fact]
    public void RefusesAFileOutsideTheFolderItIsSaidToBeIn()
    {
        using var at = new ImportFixture();
        var file = at.Write(at.Path("Elsewhere", "secret"), "x");
        Assert.Throws<IOException>(() => ImportRead.Data(file, at.Path("Local")));
        Assert.Null(ImportRead.TryData(file, at.Path("Local")));
    }

    [Fact]
    public void RefusesAFileLargerThanTheCap()
    {
        using var at = new ImportFixture();
        var file = at.Write(at.Path("Local", "big"), new byte[2048]);
        Assert.Throws<IOException>(() => ImportRead.Data(file, at.Folder, limit: 1024));
        Assert.Equal(2048, ImportRead.Data(file, at.Folder, limit: 4096).Length);
    }

    [Fact]
    public void AMissingFileIsNotFound()
    {
        using var at = new ImportFixture();
        Assert.Throws<FileNotFoundException>(() => ImportRead.Data(at.Path("Local", "none"), at.Folder));
    }

    [Fact]
    public void AFolderIsNoFile()
    {
        using var at = new ImportFixture();
        Directory.CreateDirectory(at.Path("Local", "dir"));
        Assert.Null(ImportRead.TryData(at.Path("Local", "dir"), at.Folder));
    }

    [Fact]
    public void AJunctionLeadingOutOfTheFolderIsNotFollowed()
    {
        using var at = new ImportFixture();
        at.Write(at.Path("Outside", "Bookmarks"), "{\"stolen\":true}");
        var link = at.Path("Local", "User Data", "Default");
        Directory.CreateDirectory(at.Path("Local", "User Data"));
        // A junction needs no privilege; where even that can't be made, there is nothing to test.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{at.Path("Outside")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!) mklink.WaitForExit();
        if (!Directory.Exists(link)) return;
        Assert.True(File.Exists(Path.Combine(link, "Bookmarks")));
        Assert.Null(ImportRead.TryData(Path.Combine(link, "Bookmarks"), at.Path("Local", "User Data")));
        Assert.False(ImportRead.Inside(link, at.Path("Local", "User Data")));
    }

    [Fact]
    public void AFileLinkIsNotFollowed()
    {
        using var at = new ImportFixture();
        var target = at.Write(at.Path("Local", "real"), "x");
        var link = at.Path("Local", "link");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Assert.Null(ImportRead.TryData(link, at.Folder));
        Assert.NotNull(ImportRead.TryData(target, at.Folder));
    }

    [Fact]
    public void ASnapshotReadsWhatIsStillInTheWal()
    {
        using var at = new ImportFixture();
        var file = at.Path("Local", "History");
        ImportFixture.Db(file, "PRAGMA journal_mode=WAL", "CREATE TABLE urls (url TEXT)");
        // A writer still open, as the browser is: what it wrote is in History-wal, not yet in History.
        using var browser = Sqlite.Open(file, readOnly: false)!;
        browser.Exec("PRAGMA wal_autocheckpoint=0");
        browser.Run("INSERT INTO urls VALUES (?)", "https://fresh.example/");
        Assert.True(File.Exists(file + "-wal"));
        using var copy = new Snapshot(file, at.Folder);
        Assert.NotEqual(file, copy.File);
        using var db = copy.Open()!;
        Assert.Equal(["https://fresh.example/"], db.Rows("SELECT url FROM urls").Select(r => r.Text(0)));
    }

    [Fact]
    public void ASnapshotGoesWithItsFolder()
    {
        using var at = new ImportFixture();
        var file = ImportFixture.Db(at.Path("Local", "History"), "CREATE TABLE t (n INTEGER)");
        string folder;
        using (var copy = new Snapshot(file, at.Folder)) folder = Path.GetDirectoryName(copy.File)!;
        Assert.False(Directory.Exists(folder));
        Assert.StartsWith($"search-import-{Environment.ProcessId}-", Path.GetFileName(folder));
    }

    [Fact]
    public void SweepTakesOnlyADeadProcesssLeftovers()
    {
        using var at = new ImportFixture();
        var temp = at.Path("Temp");
        var dead = Directory.CreateDirectory(Path.Combine(temp, $"search-import-{DeadPid()}-abc")).FullName;
        var mine = Directory.CreateDirectory(Path.Combine(temp, $"search-import-{Environment.ProcessId}-abc")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(temp, "search-import-unnamed")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(temp, "something-else-1-2")).FullName;
        ImportRead.Sweep(temp);
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(mine));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(other));
    }

    private static int DeadPid()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://me:secret@example.com/x", "https://example.com/x")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/x.html", null)]
    [InlineData("chrome://settings", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void OnlyWebPagesComeAndWithoutAPassword(string text, string? expected) => Assert.Equal(expected, ImportUrl.Page(text)?.AbsoluteUri);
}

public class MozLz4Tests
{
    [Fact]
    public void UnpacksLiteralsAlone()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 300) + "end");
        Assert.Equal(data, MozLz4.Unpack(ImportFixture.Lz4Literals(data), data.Length));
    }

    [Fact]
    public void UnpacksAMatchThatOverlapsWhatItCopies()
    {
        // "ab", then a match of 8 from two back: "abababab", then the last literals "!".
        byte[] block = [0x24, (byte)'a', (byte)'b', 0x02, 0x00, 0x10, (byte)'!'];
        Assert.Equal("ababababab!", Encoding.ASCII.GetString(MozLz4.Unpack(block, 11)!));
    }

    [Fact]
    public void RefusesAnOffsetBeforeTheStart()
    {
        byte[] block = [0x14, (byte)'a', 0x05, 0x00, 0x10, (byte)'!'];
        Assert.Null(MozLz4.Unpack(block, 10));
    }

    [Fact]
    public void RefusesASizeThatDoesntMatch()
    {
        var block = ImportFixture.Lz4Literals("hello"u8.ToArray());
        Assert.Null(MozLz4.Unpack(block, 4));
        Assert.Null(MozLz4.Unpack(block, 6));
        Assert.Null(MozLz4.Unpack(block[..^1], 5));
    }

    [Fact]
    public void DecodesAFileWithItsMagic()
    {
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(MozLz4.Decode(ImportFixture.JsonLz4("{\"a\":1}"))!));
        Assert.Null(MozLz4.Decode("not mozlz4 at all"u8));
    }

    [Fact]
    public void RefusesAHeaderClaimingMoreThanAnySession()
    {
        var file = ImportFixture.JsonLz4("{}");
        file[11] = 0x7f;
        Assert.Null(MozLz4.Decode(file));
    }
}
