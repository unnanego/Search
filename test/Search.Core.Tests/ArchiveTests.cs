using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class ArchiveTests
{
    private const string One = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Two = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public ArchiveTests() => TempStore.Ready();

    /// As a Mac writes it: ISO 8601 dates, slashes escaped, oldest second; and one past the month, which goes.
    private const string MacFile = $$"""
        [{"id":"{{One}}","url":"https:\/\/news.example.com\/story?id=1","title":"A story","closed":"2026-11-21T09:30:00Z"},
         {"id":"{{Two}}","url":"https:\/\/www.example.com\/","title":"","closed":"2026-11-20T23:00:00+01:00"},
         {"id":"1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB","url":"https:\/\/old.example\/","title":"Old","closed":"2026-09-01T00:00:00Z"}]
        """;

    private static (Archive Archive, string File) Fresh(string? contents = null)
    {
        var file = TempStore.Unique("archive");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Archive(file, Now), file);
    }

    [Fact]
    public void AMacFileReads()
    {
        var (archive, _) = Fresh(MacFile);
        Assert.Equal(2, archive.Tabs.Count);
        var story = archive.Tabs[0];
        Assert.Equal((Guid.Parse(One), "https://news.example.com/story?id=1", "A story"), (story.Id, story.Url.OriginalString, story.Title));
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero), story.Closed);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero), archive.Tabs[1].Closed);
        Assert.Equal(("example.com", "example.com"), (archive.Tabs[1].Label, archive.Tabs[1].Site));
        Assert.Equal(("A story", "news.example.com"), (story.Label, story.Site));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var json = Encoding.UTF8.GetString(Archive.Encode([new Archived(Guid.Parse(One), new Uri("https://a.example/x"), "X", new DateTimeOffset(2026, 11, 21, 10, 15, 30, 500, TimeSpan.FromHours(2)))]));
        Assert.Equal($$"""[{"id":"{{One}}","url":"https:\/\/a.example\/x","title":"X","closed":"2026-11-21T08:15:30Z"}]""", json);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"" + One + "\",\"url\":\"https://a/\",\"title\":\"t\",\"closed\":800000000}]")]
    [InlineData("[{\"id\":\"" + One + "\",\"url\":\"https://a/\",\"closed\":\"2026-11-21T09:30:00Z\"}]")]
    [InlineData("not json")]
    public void AFileThatWontDecodeIsSetAside(string contents)
    {
        var (archive, file) = Fresh(contents);
        Assert.Empty(archive.Tabs);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, Path.GetFileNameWithoutExtension(file) + ".unreadable-*.json"));
    }

    [Fact]
    public async Task AddingPutsItFirstOnce()
    {
        var (archive, file) = Fresh(MacFile);
        archive.Add(new Uri("https://www.example.com/"), "Again", Now, Now);
        archive.Add(new Uri("https://me:secret@private.example/inbox"), "Mail", Now.AddMinutes(1), Now);
        Assert.Equal(new[] { "https://private.example/inbox", "https://www.example.com/", "https://news.example.com/story?id=1" }, archive.Tabs.Select(t => t.Url.AbsoluteUri));
        Assert.Equal("Again", archive.Tabs[1].Title);
        await Disk.Flush();
        var written = Archive.Decode(await File.ReadAllBytesAsync(Store.File(file)))!;
        Assert.Equal(archive.Tabs.Select(t => (t.Id, t.Title)), written.Select(t => (t.Id, t.Title)));
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Store.File(file)));
    }

    [Fact]
    public void AMonthAndFiveHundredAtMost()
    {
        var list = Enumerable.Range(0, 600).Select(i => new Archived(Guid.NewGuid(), new Uri($"https://e.example/{i}"), "", Now.AddMinutes(-i))).Append(new Archived(Guid.NewGuid(), new Uri("https://old/"), "", Now - Archive.Lasts - TimeSpan.FromSeconds(1)));
        var kept = Archive.Trimmed(list.Reverse(), Now);
        Assert.Equal(Archive.Cap, kept.Count);
        Assert.Equal("https://e.example/0", kept[0].Url.AbsoluteUri);
        Assert.Single(Archive.Trimmed([new Archived(Guid.NewGuid(), new Uri("https://edge/"), "", Now - Archive.Lasts)], Now));
    }

    [Fact]
    public void TheListSearchesAndForgets()
    {
        var (archive, _) = Fresh(MacFile);
        Assert.Equal(2, archive.Listed(now: Now).Count);
        Assert.Equal("A story", Assert.Single(archive.Listed("STORY news", Now)).Title);
        Assert.Empty(archive.Listed("story nowhere", Now));
        Assert.Single(archive.Listed(now: new DateTimeOffset(2026, 12, 20, 23, 0, 0, TimeSpan.Zero)));
        var changed = 0;
        archive.PropertyChanged += (_, _) => changed++;
        archive.Remove(archive.Tabs[0].Id);
        archive.Remove(Guid.NewGuid());
        Assert.Single(archive.Tabs);
        archive.Clear();
        archive.Clear();
        Assert.Empty(archive.Tabs);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void OnlyPagesAndFilesAndNeverPrivate()
    {
        Assert.NotNull(Archive.Takes(new Uri("https://a/"), false));
        Assert.NotNull(Archive.Takes(new Uri("file:///C:/a.pdf"), false));
        Assert.Null(Archive.Takes(new Uri("https://a/"), true));
        Assert.Null(Archive.Takes(new Uri("search://history"), false));
        Assert.Null(Archive.Takes(null, false));
        Assert.Equal("archive.json", Archive.FileName(Pins.PersonalProfile));
        Assert.Equal("archive-0E984725-C51C-4BF4-9960-E1C80E27ABA0.json", Archive.FileName(Guid.Parse(One)));
    }

    [Fact]
    public async Task AForgottenProfileTakesItsArchive()
    {
        var profile = Guid.NewGuid();
        var archive = Archive.Of(profile);
        Assert.Same(archive, Archive.Of(profile));
        archive.Add(new Uri("https://a.example/"), "A");
        await Disk.Flush();
        Assert.True(File.Exists(Store.File(Archive.FileName(profile))));
        Archive.Forget(profile);
        archive.Add(new Uri("https://b.example/"), "B");
        await Disk.Flush();
        Assert.False(File.Exists(Store.File(Archive.FileName(profile))));
        Assert.NotSame(archive, Archive.Of(profile));
    }
}
