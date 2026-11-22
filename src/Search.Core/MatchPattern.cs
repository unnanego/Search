namespace Search.Core;

// Chrome's match patterns, the way an extension names the sites it wants (<all_urls>, *://*/*,
// https://*.example.com/mail/*, file:///*), read by Chrome's rules (extensions/common/url_pattern.cc).
//
// Search needs to read them itself on Windows: WebView2 grants an extension whatever its manifest says, so "Runs on"
// works by rewriting the manifest of the copy it loads (SiteAccess.Restrict), and that has to know which of the
// patterns asked for reach the sites you named.
//
// Scheme "*" is http and https, and ws and wss with them: Chrome checks a WebSocket against the same host
// permissions as the page's own requests. Only "*" stretches that way; a pattern naming http is http alone.
// The path is matched against the path and the query, as Chrome does, with "*" the only wildcard ("?" is literal).
public sealed record MatchPattern
{
    public const string AllUrlsText = "<all_urls>";

    private static readonly HashSet<string> _schemes = ["*", "http", "https", "ws", "wss", "ftp", "file", "urn", "chrome-extension"];
    private static readonly HashSet<string> _anyScheme = ["http", "https", "ws", "wss"];
    private static readonly HashSet<string> _allUrls = ["http", "https", "ws", "wss", "ftp", "file"];
    /// The schemes that name websites; a pattern for files or extension pages reaches no host you could name.
    private static readonly HashSet<string> _web = ["*", "http", "https", "ws", "wss", "ftp"];

    /// The pattern as it was written.
    public string Text { get; private init; } = "";
    /// Lower case; "*" for <all_urls> and for "*://".
    public string Scheme { get; private init; } = "";
    /// Lower case: "*", "*.example.com", "example.com", or "" for a file pattern.
    public string Host { get; private init; } = "";
    /// Null when none was written (any port), "*" for any, or digits.
    public string? Port { get; private init; }
    public string Path { get; private init; } = "";
    public bool AllUrls { get; private init; }

    /// Every website: <all_urls>, or a web scheme with host "*" (matchesAllHosts on the Mac).
    public bool AllHosts => AllUrls || (Host == "*" && _web.Contains(Scheme));
    /// Whether it names websites at all, rather than only files or extension pages.
    public bool Web => AllUrls || _web.Contains(Scheme);

    private MatchPattern() { }

    /// Null for what Chrome wouldn't take as a pattern.
    public static MatchPattern? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text == AllUrlsText) return new MatchPattern { Text = text, Scheme = "*", Host = "*", Path = "/*", AllUrls = true };
        var split = text.IndexOf("://", StringComparison.Ordinal);
        if (split <= 0) return null;
        var scheme = text[..split].ToLowerInvariant();
        if (!_schemes.Contains(scheme)) return null;
        var rest = text[(split + 3)..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var authority = rest[..slash].ToLowerInvariant();
        var path = rest[slash..];
        if (scheme == "file") return new MatchPattern { Text = text, Scheme = scheme, Host = "", Path = path };
        string? port = null;
        var bracket = authority.StartsWith('[') ? authority.IndexOf(']') : -1;
        var colon = authority.IndexOf(':', bracket < 0 ? 0 : bracket);
        if (colon >= 0)
        {
            port = authority[(colon + 1)..];
            authority = authority[..colon];
            if (port != "*" && (port.Length == 0 || !port.All(char.IsAsciiDigit))) return null;
        }
        if (authority.Length == 0) return null;
        // A wildcard only as the whole host or the first label.
        if (authority.LastIndexOf('*') is var star and >= 0 && (star > 0 || (authority.Length > 1 && authority[1] != '.') || authority == "*.")) return null;
        return new MatchPattern { Text = text, Scheme = scheme, Host = authority, Port = port, Path = path };
    }

    public static bool IsPattern(string? text) => Parse(text) is not null;

    public bool Matches(Uri url)
    {
        if (!url.IsAbsoluteUri) return false;
        var scheme = url.Scheme.ToLowerInvariant();
        if (!(AllUrls ? _allUrls.Contains(scheme) : Scheme == "*" ? _anyScheme.Contains(scheme) : Scheme == scheme)) return false;
        if (scheme != "file")
        {
            if (!MatchesHost(url.Host.ToLowerInvariant().TrimEnd('.'))) return false;
            if (Port is { } port && port != "*" && port != url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        }
        // A Windows file's path is "C:/…" to Uri and "/C:/…" to Chrome.
        var path = url.AbsolutePath.StartsWith('/') ? url.AbsolutePath : "/" + url.AbsolutePath;
        return Glob(AllUrls ? "/*" : Path, path + url.Query);
    }

    private bool MatchesHost(string host)
    {
        if (host.Length == 0) return false;
        if (Host == "*") return true;
        if (Host.StartsWith("*.", StringComparison.Ordinal)) { var bare = Host[2..]; return host == bare || host.EndsWith("." + bare, StringComparison.Ordinal); }
        return host == Host;
    }

    /// Whether it would reach `host` or any site under it: what is left of it once you say an extension may only run
    /// on that site. example.com is reached by *://*/*, by *.example.com, by example.com and by mail.example.com.
    public bool CoversHost(string host)
    {
        if (!Web) return false;
        host = host.ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0) return false;
        if (Host == "*") return true;
        if (Host.StartsWith("*.", StringComparison.Ordinal)) { var bare = Host[2..]; return host == bare || host.EndsWith("." + bare, StringComparison.Ordinal) || bare.EndsWith("." + host, StringComparison.Ordinal); }
        return Host == host || Host.EndsWith("." + host, StringComparison.Ordinal);
    }

    /// "*" matching anything, slashes included, and every other character itself.
    internal static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public override string ToString() => Text;
}
