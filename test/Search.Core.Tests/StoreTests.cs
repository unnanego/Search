using Search.Core;

namespace Search.Core.Tests;

public class StoreTests
{
    private const string Installed = @"C:\Program Files\Search\Search.exe";
    private const string DevBuild = @"C:\dev\Search\src\Search.App\bin\x64\Debug\Search.exe";

    [Fact] public void InstalledRunIsTheRealBrowser() => Assert.Null(Store.WorldFor(null, Installed));
    [Fact] public void BuildFolderRunIsTheTestWorld() => Assert.Equal("test", Store.WorldFor(null, DevBuild));
    [Fact] public void ProbeOneIsTheTestWorld() => Assert.Equal("test", Store.WorldFor("1", Installed));
    [Fact] public void NamedProbeGetsAWorldOfItsOwn() => Assert.Equal("split-view", Store.WorldFor("split-view", Installed));
    [Fact] public void ProbeNameKeepsOnlyLettersDigitsAndDashes() => Assert.Equal("ab-1", Store.WorldFor("A b-1/..", Installed));
}

public class DiskTests
{
    [Fact]
    public async Task NewestWriteWins()
    {
        var file = Path.Combine(Path.GetTempPath(), $"search-disk-{Guid.NewGuid():N}.json");
        try
        {
            var slow = Disk.Write(file, () => { Thread.Sleep(50); return "old"u8.ToArray(); });
            _ = Disk.Write(file, () => "new"u8.ToArray(), now: true);
            await slow;
            await Disk.Flush();
            Assert.Equal("new", await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task OvertakenWriteIsDropped()
    {
        var file = Path.Combine(Path.GetTempPath(), $"search-disk-{Guid.NewGuid():N}.json");
        var encoded = 0;
        try
        {
            _ = Disk.Write(file, () => { Interlocked.Increment(ref encoded); return "1"u8.ToArray(); });
            _ = Disk.Write(file, () => { Interlocked.Increment(ref encoded); return "2"u8.ToArray(); });
            await Disk.Flush();
            Assert.Equal("2", await File.ReadAllTextAsync(file));
            Assert.True(encoded <= 2);
        }
        finally { File.Delete(file); }
    }
}
