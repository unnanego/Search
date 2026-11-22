using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Core.Tests;

public class ExtensionPrepTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();
    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    private static string? Text(JsonNode? node) => node?.GetValue<string>();
    private static readonly Dictionary<string, string> NoFiles = [];

    // The wrapper.

    [Fact]
    public void AClassicWorkerIsImportedAfterTheShim() =>
        Assert.Equal(("search-browser-worker.js", "importScripts('/search-browser/shim.js', './background.js');\n"), ExtensionPrep.Wrapper(Json("""{"manifest_version":3,"background":{"service_worker":"background.js"}}""")));

    [Fact]
    public void AModuleWorkerIsImportedAfterTheShim() =>
        Assert.Equal(("search-browser-worker.js", "import '/search-browser/shim.js';\nimport './background.js';\n"), ExtensionPrep.Wrapper(Json("""{"background":{"service_worker":"background.js","type":"module"}}""")));

    [Theory]
    [InlineData("js/bg/worker.js", "js/bg/search-browser-worker.js")]
    [InlineData("/js/worker.js", "js/search-browser-worker.js")]
    [InlineData("./js/worker.js", "js/search-browser-worker.js")]
    [InlineData("js\\worker.js", "js/search-browser-worker.js")]
    public void TheWrapperSitsBesideAWorkerInAFolder(string worker, string wrapper)
    {
        var made = ExtensionPrep.Wrapper(Json($$$"""{"background":{"service_worker":"{{{worker.Replace("\\", "\\\\")}}}"}}"""))!.Value;
        Assert.Equal(wrapper, made.RelativePath);
        Assert.Equal("importScripts('/search-browser/shim.js', './worker.js');\n", made.Text);
    }

    [Fact]
    public void AQuoteInTheNameIsEscaped() => Assert.Equal("importScripts('/search-browser/shim.js', './it\\'s.js');\n", ExtensionPrep.Wrapper(Json("""{"background":{"service_worker":"it's.js"}}"""))!.Value.Text);

    [Fact] public void ManifestV2ScriptsHaveNoWrapper() => Assert.Null(ExtensionPrep.Wrapper(Json("""{"manifest_version":2,"background":{"scripts":["a.js"]}}""")));
    [Fact] public void NoBackgroundHasNoWrapper() => Assert.Null(ExtensionPrep.Wrapper(Json("""{"manifest_version":3}""")));

    // The prepared manifest.

    [Fact]
    public void TheWorkerIsPointedAtTheWrapperAndTheKeyPutIn()
    {
        var original = Json("""{"manifest_version":3,"name":"X","background":{"service_worker":"js/sw.js","type":"module"}}""");
        var prepared = ExtensionPrep.Prepared(original, "KEY", new SiteAccess());
        Assert.Equal("js/search-browser-worker.js", Text(prepared["background"]!["service_worker"]));
        Assert.Equal("module", Text(prepared["background"]!["type"]));
        Assert.Equal("KEY", Text(prepared["key"]));
        Assert.Equal("js/sw.js", Text(original["background"]!["service_worker"]));
        Assert.Null(original["key"]);
    }

    [Fact]
    public void ManifestV2GetsTheShimFirstInItsScripts()
    {
        var original = Json("""{"manifest_version":2,"background":{"scripts":["a.js","b.js"],"persistent":false}}""");
        var prepared = ExtensionPrep.Prepared(original, null, new SiteAccess());
        Assert.Equal(["search-browser/shim.js", "a.js", "b.js"], Strings(prepared["background"]!["scripts"]));
        Assert.False(prepared["background"]!["persistent"]!.GetValue<bool>());
        Assert.Equal(["search-browser/shim.js", "a.js", "b.js"], Strings(ExtensionPrep.Prepared(prepared, null, new SiteAccess())["background"]!["scripts"]));
    }

    [Fact]
    public void NoBackgroundIsLeftWithout()
    {
        var prepared = ExtensionPrep.Prepared(Json("""{"manifest_version":3,"name":"X"}"""), null, new SiteAccess());
        Assert.Null(prepared["background"]);
        Assert.Null(prepared["key"]);
    }

    [Fact]
    public void WithoutAKeyTheOwnStays() => Assert.Equal("OWN", Text(ExtensionPrep.Prepared(Json("""{"key":"OWN"}"""), null, new SiteAccess())["key"]));

    [Fact]
    public void RunsOnIsAppliedToo()
    {
        var prepared = ExtensionPrep.Prepared(Json("""{"host_permissions":["<all_urls>"],"background":{"service_worker":"sw.js"}}"""), null, new SiteAccess(SiteMode.Some, ["example.com"]));
        Assert.Equal(["*://*.example.com/*"], Strings(prepared["host_permissions"]));
        Assert.Equal("search-browser-worker.js", Text(prepared["background"]!["service_worker"]));
    }

    // Written.

    private static readonly JsonObject Original = Json("""{"manifest_version":3,"name":"Ext","host_permissions":["<all_urls>"],"background":{"service_worker":"js/sw.js"}}""");

    [Fact]
    public void WriteLeavesEverythingTheCopyNeeds()
    {
        var folder = Scratch.Folder("prep");
        Assert.Null(ExtensionPrep.ReadOriginal(folder));
        ExtensionPrep.Write(folder, Original, "KEY", new SiteAccess(SiteMode.Click), "/* shim */", new Dictionary<string, string> { ["search-browser/bridge.html"] = "<p>bridge</p>" });
        Assert.Equal("/* shim */", File.ReadAllText(Path.Combine(folder, "search-browser", "shim.js")));
        Assert.Equal("<p>bridge</p>", File.ReadAllText(Path.Combine(folder, "search-browser", "bridge.html")));
        Assert.Equal("importScripts('/search-browser/shim.js', './sw.js');\n", File.ReadAllText(Path.Combine(folder, "js", "search-browser-worker.js")));
        Assert.Equal(Original.ToJsonString(), ExtensionPrep.ReadOriginal(folder)!.ToJsonString());
        var manifest = Manifest.ReadObject(Path.Combine(folder, "manifest.json"))!;
        Assert.Equal(ExtensionPrep.Prepared(Original, "KEY", new SiteAccess(SiteMode.Click)).ToJsonString(), manifest.ToJsonString());
        Assert.Null(manifest["host_permissions"]);
    }

    [Fact]
    public void PreparingAgainStartsFromTheExtensionsOwnManifest()
    {
        var folder = Scratch.Folder("prep");
        ExtensionPrep.Write(folder, Original, "KEY", new SiteAccess(SiteMode.Click), "", NoFiles);
        // Handed the rewritten manifest the second time, as a caller reading manifest.json would: the kept one wins.
        var rewritten = Manifest.ReadObject(Path.Combine(folder, "manifest.json"))!;
        ExtensionPrep.Write(folder, rewritten, "KEY", new SiteAccess(), "", NoFiles);
        var manifest = Manifest.ReadObject(Path.Combine(folder, "manifest.json"))!;
        Assert.Equal(["<all_urls>"], Strings(manifest["host_permissions"]));
        Assert.Equal("js/search-browser-worker.js", Text(manifest["background"]!["service_worker"]));
        Assert.Equal(Original.ToJsonString(), ExtensionPrep.ReadOriginal(folder)!.ToJsonString());
        Assert.Equal("Ext", Manifest.Read(folder)!.Name);
    }

    [Theory]
    [InlineData("../outside.html")]
    [InlineData("C:/outside.html")]
    [InlineData("/outside.html")]
    public void AnExtraFileOutsideTheFolderIsRefused(string path) =>
        Assert.Throws<ArgumentException>(() => ExtensionPrep.Write(Scratch.Folder("prep"), Original, null, new SiteAccess(), "", new Dictionary<string, string> { [path] = "x" }));

    // Keys.

    [Fact]
    public void ANewKeyIsAnRsaPublicKeyWithAnIdOfItsOwn()
    {
        var key = ExtensionPrep.NewKey();
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
        Assert.Equal(2048, rsa.KeySize);
        var id = ExtensionPrep.IdOfKeyBase64(key);
        Assert.Equal(id, Crx.Id(id));
        Assert.Equal(Crx.IdOfKey(Convert.FromBase64String(key)), id);
        Assert.NotEqual(id, ExtensionPrep.IdOfKeyBase64(ExtensionPrep.NewKey()));
    }

    [Fact]
    public void AKeyWrittenAcrossLinesGivesTheSameId()
    {
        var key = ExtensionPrep.NewKey();
        Assert.Equal(ExtensionPrep.IdOfKeyBase64(key), ExtensionPrep.IdOfKeyBase64(key[..64] + "\n" + key[64..]));
    }
}
