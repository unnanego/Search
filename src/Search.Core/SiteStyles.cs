using System.Text;
using System.Text.Json;

namespace Search.Core;

// Your own style for a site, as Arc's Boosts are (SiteStyles.swift, the storage side): CSS you write, put on every page
// of that site. CSS only; Search never runs script you type into a page.
//
// Off until Settings › Browsing › Pages › Your own style for a site. Kept by site the way hidden elements are (the host
// without www., and only for a website), in styles.json beside hidden.json, the Mac's {"host": "css"}. Put in the same
// way, a stylesheet before the page has drawn. Turning the setting off takes every style off the pages and keeps them.
public sealed class SiteStyles : Observable
{
    /// The element the sheet lives in, apart from the one that hides things.
    public const string Element = "office-style";

    private readonly string _file;
    private Dictionary<string, string> _byHost = [];
    private bool _on;

    public SiteStyles(string file = "styles.json") { _file = Store.File(file); Load(); }

    /// A site whose style changed, for every window to pass on to its open pages of that site; null for all of them,
    /// when the switch moved.
    public event Action<string?>? StyleChanged;

    /// Settings › Browsing › Pages. Off, pages get nothing at all.
    public bool On { get => _on; set { if (Set(ref _on, value)) StyleChanged?.Invoke(null); } }

    public IReadOnlyDictionary<string, string> ByHost => _byHost;

    /// The sites that have a style, in order.
    public IReadOnlyList<string> Hosts => _byHost.Keys.Order(StringComparer.Ordinal).ToList();

    /// The same host hidden elements and dark pages are kept by: no www., and only for a website.
    public static string? Host(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || !Address.HasHost(url)) return null;
        var host = url.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// What a page of this site is to wear: nothing while the setting is off.
    public string? Css(string? host) => _on && host is not null && _byHost.TryGetValue(host, out var css) && css.Length > 0 ? css : null;

    /// What was written for a site, on or off.
    public string Written(string host) => _byHost.GetValueOrDefault(host, "");

    /// Nothing but whitespace is no style at all, and the site is forgotten.
    public void Set(string css, string host)
    {
        var kept = string.IsNullOrWhiteSpace(css) ? null : css;
        if (_byHost.GetValueOrDefault(host) == kept) return;
        var next = new Dictionary<string, string>(_byHost);
        if (kept is null) next.Remove(host); else next[host] = kept;
        _byHost = next;
        Changed(nameof(ByHost));
        Changed(nameof(Hosts));
        // One after another, the newest last (see Disk): typing writes a few times a second, and only the last lands.
        Disk.Write(_file, () => Encode(next));
        StyleChanged?.Invoke(host);
    }

    public void Remove(string host) => Set("", host);

    // Into the page.

    /// Puts the sheet in, or empties it. Last in the document once there is one, so a rule of yours wins over the
    /// site's own of the same weight without needing !important. Text in a style element, never markup.
    private const string Apply = $$"""
        function (css) {
          var sheet = document.getElementById('{{Element}}');
          if (!sheet) {
            if (!css) return;
            sheet = document.createElement('style');
            sheet.id = '{{Element}}';
          }
          sheet.textContent = css;
          var last = function () { document.documentElement.appendChild(sheet); };
          last();
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', last, { once: true });
        }
        """;

    /// For a page about to load: the sheet as a string the script can't be broken out of, whatever quotes, backticks
    /// or `${` it holds.
    public static string Script(string css) => $"({Apply})({Literal(css)});";

    /// For the page up now, where the CSS travels as an argument named `css` and is never part of the source.
    public const string Live = $"({Apply})(css);";

    /// A JavaScript string literal: JSON's, as Foundation writes it (slashes escaped), with the two line separators
    /// and `<` spelled out as well.
    public static string Literal(string text)
    {
        var literal = new StringBuilder("\"", text.Length + 2);
        foreach (var c in text)
            literal.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '/' => "\\/", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                (char)0x2028 => "\\u2028", (char)0x2029 => "\\u2029", '<' => "\\u003c",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return literal.Append('"').ToString();
    }

    // The file.

    private void Load()
    {
        byte[] data;
        try { data = File.ReadAllBytes(_file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (Decode(data) is { } stored) _byHost = stored; else Store.Quarantine(_file);
    }

    public static byte[] Encode(IReadOnlyDictionary<string, string> byHost) => SwiftJson.Encode(w =>
    {
        w.WriteStartObject();
        foreach (var (host, css) in byHost) w.WriteString(host, css);
        w.WriteEndObject();
    });

    /// Null when the file isn't an object of words.
    public static Dictionary<string, string>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var byHost = new Dictionary<string, string>();
            foreach (var site in document.RootElement.EnumerateObject())
                if (SwiftJson.Text(site.Value) is { } css) byHost[site.Name] = css; else return null;
            return byHost;
        }
        catch (JsonException) { return null; }
    }
}
