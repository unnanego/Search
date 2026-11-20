using System.Globalization;
using System.Text;

namespace Search.Core;

// What you type has to be a place (Address.swift). There is no search here, so this either hands back a URL or hands
// back nothing, and nothing is worth saying out loud: the alternative is a browser that silently does something
// else with your keystrokes.
//
// System.Uri normalises what it parses (a "/" path, a lowercase host, some escapes undone), so addresses are
// compared here as Uri.AbsoluteUri, never as typed.
public static class Address
{
    /// The window's own pages: search://settings and the like.
    public const string InternalScheme = "search";

    /// Schemes the window can show itself. Anything else typed with a scheme (mailto:, a custom app link) is
    /// somebody else's job and gets refused here rather than opening a blank tab.
    private static readonly HashSet<string> Ours = ["http", "https", "file", "about", "data", InternalScheme];

    /// A file on this PC, typed as C:\…, C:/… or ~\…. Only committed navigation checks the disk; suggestions and
    /// dropped text never do.
    public static Uri? File(string typed)
    {
        var text = typed.Trim();
        var drive = text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/';
        var home = text.StartsWith(@"~\", StringComparison.Ordinal) || text.StartsWith("~/", StringComparison.Ordinal);
        if (!drive && !home) return null;
        var path = home ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..]) : text;
        try
        {
            path = Path.GetFullPath(path);
            return System.IO.File.Exists(path) ? new Uri(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UriFormatException) { return null; }
    }

    public static Uri? UrlFrom(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;

        // Written with a scheme, it is taken at its word.
        var split = text.IndexOf("://", StringComparison.Ordinal);
        if (split >= 0) return Ours.Contains(text[..split].ToLowerInvariant()) ? Parse(text) : null;
        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return Parse(text);

        // Everything else has to look like a host before it gets a scheme put in front of it. "hello world" is not
        // a website, and neither is "todo".
        var end = text.IndexOfAny(['/', '?', '#']);
        var head = end < 0 ? text : text[..end];
        if (head.Contains('@')) return null;   // an email address
        // This PC by its IPv6 address, [::1]:3000, is a place too.
        if (head.StartsWith("[::1]", StringComparison.Ordinal)) return Parse("http://" + text);
        var host = head.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? head;
        if (!LooksLikeHost(host)) return null;

        // A local server almost never has a certificate, so https there is a connection failure rather than a
        // page. The same goes for a device on the network by its name, printer.local or homeassistant.local.
        var local = host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || host == "127.0.0.1" || host == "0.0.0.0" || PrivateRange(host);
        return Parse((local ? "http://" : "https://") + text);
    }

    /// Whether it has a host, as URL.host() would: mailto:a@b.c doesn't, though Uri.Host says "b.c".
    public static bool HasHost(Uri url) => url.IsAbsoluteUri && url.Host.Length > 0 && url.AbsoluteUri.StartsWith(url.Scheme + "://", StringComparison.OrdinalIgnoreCase);

    private static Uri? Parse(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    private static bool Octet(string part) => part.Length > 0 && byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// The private ranges, where Docker and many offices put their machines. Four numbers and nothing more: 10.io
    /// is a website, not 10.0.0.0/8.
    private static bool PrivateRange(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4 || !parts.All(Octet)) return false;
        var octets = parts.Select(p => byte.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        return octets[0] == 10 || (octets[0] == 192 && octets[1] == 168) || (octets[0] == 172 && octets[1] is >= 16 and <= 31);
    }

    private static bool LooksLikeHost(string host)
    {
        if (host == "localhost") return true;

        // Four numbers is an address on the local network as often as not.
        var labels = host.Split('.');
        if (labels.Length == 4 && labels.All(Octet)) return true;
        if (labels.Length < 2) return false;
        if (!labels.All(l => l.Length > 0 && l[0] != '-' && l[^1] != '-' && l.EnumerateRunes().All(r => Rune.IsLetter(r) || Rune.IsNumber(r) || r.Value == '-'))) return false;

        // The last label carries the weight: a dotted thing ending in letters is a domain, a dotted thing ending in
        // digits is a version number.
        var tld = labels[^1];
        return tld.EnumerateRunes().Count() >= 2 && tld.EnumerateRunes().All(Rune.IsLetter);
    }

    /// A page's address as the field shows it for editing: something that, sent as it is, goes back to the same
    /// page (UrlFrom gives the same AbsoluteUri). The scheme is left off only when the field would put that very
    /// scheme back, and a bare "/" only when nothing follows it: a port, a query, a fragment, plain http to
    /// somewhere that isn't this PC or the local network, all stay.
    public static string Editable(Uri page)
    {
        if (!page.IsAbsoluteUri) return page.OriginalString;
        var full = page.AbsoluteUri;
        var scheme = page.Scheme;
        if (!full.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase)) return full;
        var shortened = full[(scheme.Length + 3)..];
        List<string> candidates = [shortened];
        if (page.AbsolutePath == "/" && page.Query.Length == 0 && page.Fragment.Length == 0 && shortened.EndsWith('/')) candidates.Insert(0, shortened[..^1]);
        foreach (var candidate in candidates)
            if (UrlFrom(candidate) is { } back && back.AbsoluteUri == full) return candidate;
        return full;
    }

    /// An address as a row shows it: without a leading www.
    public static string WithoutWww(string address) => address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address[4..] : address;

    /// What the tab says before the page has told us its title: the address, with the parts nobody reads taken off.
    public static string Pretty(Uri url)
    {
        if (!HasHost(url)) return url.IsAbsoluteUri ? url.AbsoluteUri : url.OriginalString;
        var bare = url.Host.StartsWith("www.", StringComparison.Ordinal) ? url.Host[4..] : url.Host;
        var path = url.AbsolutePath;
        return path.Length == 0 || path == "/" ? bare : bare + path;
    }
}
