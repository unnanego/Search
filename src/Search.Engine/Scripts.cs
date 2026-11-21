using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Search.Engine;

// Search's own page scripts, kept as .js files in Scripts\ beside this one and embedded in the assembly (see the
// csproj, which names each Search.Engine.Scripts.<file>.js). Read once, by file name.
public static class Scripts
{
    private static readonly ConcurrentDictionary<string, string> _read = new();
    /// Script text is never inside an HTML <script> here (it goes through the DevTools protocol), so nothing needs
    /// escaping for HTML's sake, and a long article doesn't grow six times over as \u escapes.
    private static readonly JsonSerializerOptions _literal = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// A script by its file name: "curtain.js".
    public static string Read(string name) => _read.GetOrAdd(name, Load);

    /// A JavaScript string literal (JSON's) for any text, or `null`: nothing in it can end the string it's put in.
    public static string Literal(string? text) => JsonSerializer.Serialize(text, _literal);

    private static string Load(string name)
    {
        using var stream = typeof(Scripts).Assembly.GetManifestResourceStream("Search.Engine.Scripts." + name) ?? throw new InvalidOperationException($"No script named {name} is embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
