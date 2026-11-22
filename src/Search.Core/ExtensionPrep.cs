using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Search.Core;

// The copy of an extension WebView2 loads, made ready (ExtensionShims.prepare on the Mac).
//
// WebView2 loads an unpacked folder as it is, so what Search needs from the extension is written into the folder
// before it loads: the key that gives it its id, Search's shim run before the extension's own worker (the tabs and
// windows WebView2 doesn't model as Chrome does), and "Runs on" as a narrower manifest (SiteAccess.Restrict).
//
// Everything Search adds lives in search-browser in the folder (not __search: Chromium refuses any name an extension ships that starts with "_", which it keeps for itself), apart from the extension's own files, except the worker
// wrapper, which has to sit beside the extension's worker so the worker's own relative importScripts still resolve.
// The extension's own manifest is kept there untouched, written once, so preparing again (a "Runs on" change) starts
// from what the extension asked for rather than from the last rewrite.
public static class ExtensionPrep
{
    public const string OwnFolder = "search-browser";
    public const string ShimFile = "search-browser/shim.js";
    public const string OriginalFile = "search-browser/manifest.original.json";
    /// Permissions Search wrote into the manifest itself, which the extension never asked for: a JSON list of names.
    public const string AddedFile = "search-browser/added.json";
    public const string WrapperName = "search-browser-worker.js";

    private static readonly JsonSerializerOptions _written = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// The manifest the loaded copy gets: the extension's own, with `key` when given (the store's, or one of Search's
    /// own for a folder), its background pointed at Search's shim first, and its sites as "Runs on" has them.
    public static JsonObject Prepared(JsonObject original, string? key, SiteAccess access)
    {
        var prepared = SiteAccess.Restrict(original, access);
        if (key is not null) prepared["key"] = key;
        if (prepared["background"] is JsonObject background)
        {
            if (Wrapper(original) is { } wrapper) background["service_worker"] = wrapper.RelativePath;
            else if (background["scripts"] is JsonArray scripts && !scripts.Any(s => Manifest.Text(s) == ShimFile)) background["scripts"] = new JsonArray([ShimFile, .. scripts.Select(s => s?.DeepClone())]);
        }
        return prepared;
    }

    /// The worker that stands in for a Manifest V3 extension's own: beside it, running Search's shim and then it. A
    /// classic worker imports both with importScripts; a module worker with import, in order, so the shim is there
    /// before any of the extension's code runs. Null for an extension with no service worker (Manifest V2's
    /// background scripts get the shim put first in their list instead).
    public static (string RelativePath, string Text)? Wrapper(JsonObject original)
    {
        if (new Manifest(original).Worker is not { } worker) return null;
        var path = worker.Path.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        var slash = path.LastIndexOf('/');
        var (folder, name) = slash < 0 ? ("", path) : (path[..(slash + 1)], path[(slash + 1)..]);
        var shim = Quoted("/" + ShimFile);
        var own = Quoted("./" + name);
        return (folder + WrapperName, worker.Module ? $"import {shim};\nimport {own};\n" : $"importScripts({shim}, {own});\n");
    }

    /// A path in single quotes, for the wrapper's script.
    private static string Quoted(string path) => "'" + path.Replace("\\", @"\\").Replace("'", @"\'").Replace("\n", @"\n").Replace("\r", @"\r") + "'";

    /// Makes the copy in `folder` ready: keeps the extension's own manifest aside the first time (and starts from that
    /// one every later time), then writes Search's shim, the worker wrapper, `extraFiles` (paths relative to the folder,
    /// such as search-browser/bridge.html) and the prepared manifest.
    public static void Write(string folder, JsonObject original, string? key, SiteAccess access, string shimJs, IReadOnlyDictionary<string, string> extraFiles)
    {
        var source = ReadOriginal(folder);
        if (source is null) { source = original; Put(folder, OriginalFile, source.ToJsonString(_written)); }
        Put(folder, ShimFile, shimJs);
        if (Wrapper(source) is { } wrapper) Put(folder, wrapper.RelativePath, wrapper.Text);
        foreach (var (path, text) in extraFiles) Put(folder, path, text);
        Put(folder, "manifest.json", Prepared(source, key, access).ToJsonString(_written));
    }

    /// The extension's own manifest, as kept aside by Write; null before the copy was first prepared.
    public static JsonObject? ReadOriginal(string folder) => Manifest.ReadObject(Path.Combine(folder, OriginalFile.Replace('/', Path.DirectorySeparatorChar)));

    /// A file inside the folder, never outside it.
    private static void Put(string folder, string relative, string text)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative)) throw new ArgumentException($"Not a path inside the extension: {relative}", nameof(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // Keys.

    /// A new key for a folder's copy: an RSA-2048 public key (SubjectPublicKeyInfo DER) in base64, as a manifest's
    /// "key" is written. Search gives a folder copy a key of its own, so its id is stable across reloads and known
    /// before WebView2 loads it. A folder's own "key" is ignored, as on the Mac: nothing checks it and store keys are
    /// public, so a folder could otherwise take any extension's id. Only the public half is kept; nothing is signed.
    public static string NewKey() { using var rsa = RSA.Create(2048); return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()); }

    /// The id a manifest's "key" gives an extension.
    public static string IdOfKeyBase64(string key) => Crx.IdOfKey(Convert.FromBase64String(key));
}
