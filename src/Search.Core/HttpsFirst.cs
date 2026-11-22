namespace Search.Core;

// Settings › Try HTTPS first, off unless turned on (HTTPSFirst in Privacy.swift).
//
// A top-level http:// load is tried over https:// first, and over http:// again only where that fails, so a site with
// no HTTPS still loads, quietly, as before. WebView2 upgrades nothing by itself (Chrome's HTTPS-Upgrades and Edge's
// Automatic HTTPS aren't in it: a plain http:// load never so much as knocks on port 443), and a bare address typed
// into the field already gets https:// (Address); without this, an http:// link, bookmark or history entry loaded in
// the clear. The page's frames are Chromium's to decide.
//
// Local addresses (localhost, .localhost, .test, .local, IP addresses, a one-word host) are left as asked: a dev
// server rarely has HTTPS, and shouldn't wait on a failed attempt first. So is an address with a port of its own,
// which can't answer both ways on the one port.
public static class HttpsFirst
{
    /// The https:// form of a top-level http:// address worth trying first, or null to load it as asked.
    public static Uri? Upgrade(Uri url) => url is { IsAbsoluteUri: true, Scheme: "http", IsDefaultPort: true } && !Local(url) ? new UriBuilder(url) { Scheme = "https", Port = -1 }.Uri : null;

    /// An address no certificate would name, or one on this PC or its network.
    public static bool Local(Uri url)
    {
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return true;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".test", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)) return true;
        // An IP address, v4 or v6.
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || host.Contains(':') || host.All(c => char.IsAsciiDigit(c) || c == '.')) return true;
        return !host.Contains('.');
    }
}
