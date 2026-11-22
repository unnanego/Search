using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Core.Tests;

public class ManifestTests
{
    private static Manifest Of(string json) => new(JsonNode.Parse(json)!.AsObject());

    /// A folder with these files in it, paths relative to it.
    private static string FolderWith(params (string Path, string Text)[] files)
    {
        var folder = Scratch.Folder("manifest");
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return folder;
    }

    // What it is.

    [Fact]
    public void ItsPlainFieldsRead()
    {
        var m = Of("""{"manifest_version":3,"name":"Dark Reader","version":"4.9.1","description":"Dark mode for every website"}""");
        Assert.Equal((3, "Dark Reader", "4.9.1", "Dark mode for every website"), (m.ManifestVersion, m.Name, m.Version, m.Description));
    }

    [Fact]
    public void WhatIsMissingIsNull()
    {
        var m = Of("{}");
        Assert.Null(m.ManifestVersion);
        Assert.All(new[] { m.Name, m.Version, m.Description, m.Popup, m.SidePanel, m.Options, m.ActionTitle }, Assert.Null);
        Assert.Null(m.IconPath(16));
        Assert.Null(m.Worker);
        Assert.Empty(m.Permissions);
        Assert.Empty(m.HostPatterns);
        Assert.Empty(m.BackgroundScripts);
        Assert.Empty(m.Describe());
        Assert.Empty(m.Grants());
    }

    [Fact]
    public void ItsNameComesFromItsLanguagesEnglishFirst()
    {
        var folder = FolderWith(
            ("manifest.json", """{"name":"__MSG_appName__","description":"__MSG_appDesc__","default_locale":"fr","action":{"default_title":"__MSG_title__"}}"""),
            ("_locales/fr/messages.json", """{"appName":{"message":"Bonjour"},"appDesc":{"message":"Une extension"},"title":{"message":"Titre"}}"""),
            ("_locales/en/messages.json", """{"APPNAME":{"message":"Hello","description":"the name"}}"""));
        var m = Manifest.Read(folder)!;
        Assert.Equal("Hello", m.Name);
        Assert.Equal("Une extension", m.Description);
        Assert.Equal("Titre", m.ActionTitle);
    }

    [Fact]
    public void WithoutEnglishItsDefaultLocaleNamesIt()
    {
        var folder = FolderWith(("manifest.json", """{"name":"__MSG_appName__","default_locale":"de"}"""), ("_locales/de/messages.json", """{"appName":{"message":"Hallo"}}"""));
        Assert.Equal("Hallo", Manifest.Read(folder)!.Name);
    }

    [Fact]
    public void ALocaleAskedForComesFirstThenItsLanguage()
    {
        var folder = FolderWith(
            ("manifest.json", """{"name":"__MSG_n__","short_name":"x","default_locale":"en"}"""),
            ("_locales/en/messages.json", """{"n":{"message":"English"}}"""),
            ("_locales/pt/messages.json", """{"n":{"message":"Português"}}"""),
            ("_locales/pt_BR/messages.json", """{"n":{"message":"Brasil"}}"""));
        Assert.Equal("Brasil", Manifest.Read(folder, "pt-BR")!.Name);
        Assert.Equal("Português", Manifest.Read(folder, "pt_PT")!.Name);
        Assert.Equal("English", Manifest.Read(folder, "ja")!.Name);
    }

    [Fact]
    public void MessagesInsideTextAndMissingOnesAreFilledIn()
    {
        var folder = FolderWith(("manifest.json", """{"name":"Ext: __MSG_a__!__MSG_missing__ (__MSG_b__)","default_locale":"en"}"""), ("_locales/en/messages.json", """{"a":{"message":"A"},"b":{"message":"B"}}"""));
        Assert.Equal("Ext: A! (B)", Manifest.Read(folder)!.Name);
    }

    [Fact] public void WithoutAFolderAMessageIsNothing() => Assert.Equal("", Of("""{"name":"__MSG_x__"}""").Name);

    [Fact]
    public void ChromesCommentsAndTrailingCommasRead()
    {
        var folder = FolderWith(("manifest.json", "{\n  // its name\n  \"name\": \"Commented\", /* and */ \"version\": \"1\",\n}"));
        Assert.Equal("Commented", Manifest.Read(folder)!.Name);
    }

    [Fact]
    public void TheExtensionsOwnManifestIsReadRatherThanSearchsRewrite()
    {
        var folder = FolderWith(("manifest.json", """{"name":"Rewritten"}"""), (ExtensionPrep.OriginalFile, """{"name":"Own"}"""));
        Assert.Equal("Own", Manifest.Read(folder)!.Name);
    }

    [Fact] public void NoManifestIsNone() => Assert.Null(Manifest.Read(Scratch.Folder()));
    [Fact] public void ABrokenManifestIsNone() => Assert.Null(Manifest.Read(FolderWith(("manifest.json", "{not json"))));
    [Fact] public void AManifestThatIsntAnObjectIsNone() => Assert.Null(Manifest.Read(FolderWith(("manifest.json", "[1]"))));

    // Its pages.

    [Fact]
    public void ItsPagesRead()
    {
        var m = Of("""{"action":{"default_popup":"popup.html","default_title":"Open"},"side_panel":{"default_path":"panel.html"},"options_ui":{"page":"options.html"}}""");
        Assert.Equal(("popup.html", "panel.html", "options.html", "Open"), (m.Popup, m.SidePanel, m.Options, m.ActionTitle));
    }

    [Fact] public void ManifestV2sBrowserActionHasThePopup() => Assert.Equal("p.html", Of("""{"browser_action":{"default_popup":"p.html"}}""").Popup);
    [Fact] public void ManifestV2sPageActionHasThePopup() => Assert.Equal("p.html", Of("""{"page_action":{"default_popup":"p.html"}}""").Popup);
    [Fact] public void AnEmptyPopupIsNone() => Assert.Null(Of("""{"action":{"default_popup":""}}""").Popup);
    [Fact] public void TheOldOptionsPageReads() => Assert.Equal("o.html", Of("""{"options_page":"o.html"}""").Options);

    // Icons.

    private const string Icons = """{"action":{"default_icon":{"16":"/icons/a16.png","32":"icons/a32.png"}},"icons":{"48":"i48.png","128":"i128.png"}}""";

    [Theory]
    [InlineData(16, "icons/a16.png")]
    [InlineData(20, "icons/a32.png")]
    [InlineData(32, "icons/a32.png")]
    [InlineData(64, "icons/a32.png")]
    [InlineData(1, "icons/a16.png")]
    public void TheButtonsIconIsTheSmallestBigEnoughElseTheBiggest(int size, string path) => Assert.Equal(path, Of(Icons).IconPath(size));

    [Fact] public void WithoutAButtonIconItsIconsAreUsed() => Assert.Equal("i128.png", Of("""{"icons":{"48":"i48.png","128":"i128.png","x":"bad.png"}}""").IconPath(64));
    [Fact] public void OneIconForEverySize() => Assert.Equal("icon.png", Of("""{"action":{"default_icon":"icon.png"},"icons":{"16":"i16.png"}}""").IconPath(16));
    [Fact] public void ManifestV2sButtonIconReads() => Assert.Equal("b.png", Of("""{"browser_action":{"default_icon":{"19":"b.png"}}}""").IconPath(16));

    // What it asks for.

    private const string Asks = """
        {"manifest_version":2,"permissions":["tabs","<all_urls>","storage","https://api.example.com/*"],
         "optional_permissions":["history","https://*/*"],
         "host_permissions":["https://a.com/*"],
         "content_scripts":[{"matches":["https://b.com/*","https://a.com/*"]},{"js":["x.js"]}],
         "background":{"scripts":["bg1.js","bg2.js"]}}
        """;

    [Fact] public void PermissionsAreNamesNotSites() => Assert.Equal(["tabs", "storage"], Of(Asks).Permissions);
    [Fact] public void OptionalPermissionsAreNamesToo() => Assert.Equal(["history"], Of(Asks).OptionalPermissions);
    [Fact] public void HostPatternsAreEverySiteItAsksForEachOnce() => Assert.Equal(["https://a.com/*", "<all_urls>", "https://api.example.com/*", "https://b.com/*"], Of(Asks).HostPatterns);
    [Fact] public void ManifestV2BackgroundScriptsRead() => Assert.Equal(["bg1.js", "bg2.js"], Of(Asks).BackgroundScripts);

    [Fact]
    public void TheWorkerAndWhetherItIsAModuleRead()
    {
        Assert.Equal(("sw.js", false), Of("""{"background":{"service_worker":"sw.js"}}""").Worker);
        Assert.Equal(("js/sw.mjs", true), Of("""{"background":{"service_worker":"js/sw.mjs","type":"module"}}""").Worker);
        Assert.Null(Of("""{"background":{"service_worker":""}}""").Worker);
    }

    // In words.

    [Fact]
    public void EveryWebsiteIsOneSentence() =>
        Assert.Equal(["Read and change everything on every website", "See your open tabs and their addresses"], Of("""{"permissions":["tabs","storage"],"host_permissions":["*://*/*","https://a.com/*"]}""").Describe());

    [Fact]
    public void AllUrlsInAContentScriptIsEveryWebsite() => Assert.Equal(["Read and change everything on every website"], Of("""{"content_scripts":[{"matches":["<all_urls>"]}]}""").Describe());

    [Fact]
    public void NamedSitesAreTheFirstFourAndHowManyMore() =>
        Assert.Equal(["Read and change what's on a.com, *.b.com, c.com, d.com and 2 more"],
            Of("""{"host_permissions":["https://a.com/*","http://a.com/*","https://*.b.com/*","https://c.com/*","https://d.com/*","https://e.com/*","https://f.com/*"]}""").Describe());

    [Fact] public void AFewNamedSitesAreAllNamed() => Assert.Equal(["Read and change what's on a.com, b.com"], Of("""{"host_permissions":["https://a.com/*","https://b.com/*"]}""").Describe());
    [Fact] public void FilesAloneAreNoSites() => Assert.Empty(Of("""{"host_permissions":["file:///*"]}""").Describe());

    [Fact]
    public void PermissionsThenChromesOwnAreSaid()
    {
        var m = Of("""{"permissions":["scripting","nativeMessaging","history","cookies","storage","tabs","bookmarks","declarativeNetRequest"]}""");
        Assert.Equal(["See your open tabs and their addresses", "Read and change cookies", "Block or change requests pages make", "Talk to apps on this PC", "Run scripts in pages",
            "Read and change your history", "Read and change your bookmarks"], m.Describe());
    }

    [Fact]
    public void WhatSearchAddedIsntSaidOrCounted()
    {
        var folder = FolderWith(("manifest.json", """{"permissions":["nativeMessaging","tabs","history"]}"""), (ExtensionPrep.AddedFile, """["nativeMessaging","history"]"""));
        var m = Manifest.Read(folder)!;
        Assert.Equal(["See your open tabs and their addresses"], m.Describe());
        Assert.Equal(["tabs"], m.Grants());
        Assert.Equal(["history", "nativeMessaging"], m.Added.Order());
    }

    [Fact] public void ABrokenAddedListIsNothingAdded() => Assert.Empty(Manifest.Read(FolderWith(("manifest.json", "{}"), (ExtensionPrep.AddedFile, "{oops")))!.Added);

    [Fact]
    public void GrantsArePermissionsAndSitesInOrder() =>
        Assert.Equal(["site:<all_urls>", "site:https://a.com/*", "site:https://api.example.com/*", "site:https://b.com/*", "storage", "tabs"], Of(Asks).Grants());

    [Fact]
    public void AnUpdateAskingForMoreIsSeen()
    {
        var before = Of("""{"permissions":["storage"],"host_permissions":["https://a.com/*"]}""").Grants();
        var after = Of("""{"permissions":["storage","tabs"],"host_permissions":["https://a.com/*","https://b.com/*"]}""");
        Assert.Equal(["site:https://b.com/*", "tabs"], after.More(before));
        Assert.Empty(Of("""{"permissions":["storage"]}""").More(before));
    }
}
