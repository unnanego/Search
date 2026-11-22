using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class ExtensionListTests
{
    private const string Dark = "eimadpbcbfnmbkopoojfekhnkhdbieeh";
    private const string Mine = "local-0e984725";

    public ExtensionListTests() => TempStore.Ready();

    private static readonly Installed DarkReader = new(Dark, "Dark Reader", "4.9.96", true, true, ["site:<all_urls>", "storage", "tabs"]);
    private static readonly Installed MineToo = new(Mine, "Mine", "0.1", false, false, [], Pinned: true, Source: @"C:\dev\mine");

    /// As Swift's JSONEncoder writes [Installed]: compact, slashes escaped, a nil optional left out.
    private const string MacFile = """[{"id":"eimadpbcbfnmbkopoojfekhnkhdbieeh","name":"Dark Reader","version":"4.9.96","enabled":true,"fromStore":true,"permissions":["site:<all_urls>","storage","tabs"]},{"id":"local-0e984725","name":"Mine","version":"0.1","enabled":false,"fromStore":false,"permissions":[],"pinned":true,"source":"C:\\dev\\mine"}]""";

    [Fact] public void WritesTheMacsShape() => Assert.Equal(MacFile, Encoding.UTF8.GetString(ExtensionList.Encode([DarkReader, MineToo])));

    [Fact] public void SlashesAreEscapedAsFoundationDoes() => Assert.Contains(@"""site:https:\/\/a.com\/*""", Encoding.UTF8.GetString(ExtensionList.Encode([DarkReader with { Permissions = ["site:https://a.com/*"] }])));

    [Fact]
    public void AMacFileReads()
    {
        var read = ExtensionList.Decode(Encoding.UTF8.GetBytes(MacFile))!;
        Assert.Equal([DarkReader, MineToo], read);
    }

    [Fact]
    public void KeysInAnyOrderAndNullOptionalsRead()
    {
        var read = ExtensionList.Decode("""[{"permissions":["tabs"],"fromStore":true,"enabled":false,"version":"1","name":"N","id":"x","pinned":null,"source":null}]"""u8.ToArray())!;
        Assert.Equal(new Installed("x", "N", "1", false, true, ["tabs"]), Assert.Single(read));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""[{"id":"x","name":"N","version":"1","enabled":true,"fromStore":true}]""")]
    [InlineData("""[{"id":"x","name":"N","version":"1","enabled":"yes","fromStore":true,"permissions":[]}]""")]
    [InlineData("""[{"id":"x","name":"N","version":"1","enabled":true,"fromStore":true,"permissions":[1]}]""")]
    [InlineData("""[{"id":"x","name":"N","version":"1","enabled":true,"fromStore":true,"permissions":[],"pinned":"no"}]""")]
    [InlineData("""[{"id":"x","name":"N","version":"1","enabled":true,"fromStore":true,"permissions":[],"source":3}]""")]
    public void WhatTheMacWouldntDecodeDoesnt(string json) => Assert.Null(ExtensionList.Decode(Encoding.UTF8.GetBytes(json)));

    // The list.

    private static string Folder() => Scratch.Folder("extensions");

    [Fact]
    public void NoFileIsNoExtensions()
    {
        var list = new ExtensionList(Folder());
        Assert.Empty(list.Items);
    }

    [Fact]
    public void TheStoresFolderIsTheDefault() => Assert.Equal(Path.Combine(Store.Folder, "Extensions"), new ExtensionList().Folder);

    [Fact]
    public void AFileThatWontDecodeIsSetAside()
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "installed.json"), "[{]");
        Assert.Empty(new ExtensionList(folder).Items);
        Assert.False(File.Exists(Path.Combine(folder, "installed.json")));
        Assert.Single(Directory.GetFiles(folder, "installed.unreadable-*.json"));
    }

    [Fact]
    public async Task WhatIsPutIsSavedAndReadBack()
    {
        var folder = Folder();
        var list = new ExtensionList(folder);
        list.Put(DarkReader);
        list.Put(MineToo);
        await Disk.Flush();
        Assert.Equal(MacFile, await File.ReadAllTextAsync(list.ListFile));
        Assert.Equal([DarkReader, MineToo], new ExtensionList(folder).Items);
    }

    [Fact]
    public void PutReplacesInPlace()
    {
        var list = new ExtensionList(Folder());
        list.Put(DarkReader);
        list.Put(MineToo);
        list.Put(DarkReader with { Version = "5.0" });
        Assert.Equal(["5.0", "0.1"], list.Items.Select(i => i.Version));
        Assert.Equal("5.0", list.Find(Dark)!.Version);
        Assert.Null(list.Find("nope"));
    }

    [Fact]
    public void RemoveTakesOneOut()
    {
        var list = new ExtensionList(Folder());
        list.Put(DarkReader);
        list.Put(MineToo);
        Assert.True(list.Remove(Dark));
        Assert.False(list.Remove(Dark));
        Assert.Equal([MineToo], list.Items);
    }

    [Fact]
    public void PinnedAndEnabledAreSet()
    {
        var list = new ExtensionList(Folder());
        list.Put(DarkReader);
        list.SetPinned(Dark, true);
        list.SetEnabled(Dark, false);
        list.SetPinned("nope", true);
        Assert.Equal(DarkReader with { Pinned = true, Enabled = false }, Assert.Single(list.Items));
        list.SetPinned(Dark, false);
        Assert.False(list.Find(Dark)!.Pinned);
    }

    [Fact]
    public void ChangesAreTold()
    {
        var list = new ExtensionList(Folder());
        var told = new List<string?>();
        ((INotifyPropertyChanged)list).PropertyChanged += (_, e) => told.Add(e.PropertyName);
        list.Put(DarkReader);
        list.Put(DarkReader);
        list.SetEnabled(Dark, true);
        list.SetEnabled(Dark, false);
        Assert.Equal(["Items", "Items"], told);
    }

    [Fact]
    public void EachExtensionHasAFolderOfItsOwn()
    {
        var folder = Folder();
        var list = new ExtensionList(folder);
        Assert.Equal(Path.Combine(folder, Dark), list.FolderFor(Dark));
        Assert.Equal(Path.Combine(folder, "installed.json"), list.ListFile);
        Assert.Throws<ArgumentException>(() => list.FolderFor(".."));
        Assert.Throws<ArgumentException>(() => list.FolderFor(@"a\b"));
        Assert.Throws<ArgumentException>(() => list.FolderFor(""));
    }
}
