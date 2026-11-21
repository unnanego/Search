namespace Search.Core;

// Which part of a host is the site (Registrable.swift): the registrable domain, one label below a public suffix.
// The Mac asks the system's list of public suffixes; Windows has none to ask, so the test is passed in, and with none
// a host is only ever its own site, never one with a neighbour. That's the strict side to err on: a password is
// offered on fewer pages, never on more.
public static class Registrable
{
    /// The suffix test the app uses, when it has a list of public suffixes; null otherwise.
    public static Func<string, bool>? Suffixes { get; set; }

    public static string Domain(string host, Func<string, bool>? isSuffix)
    {
        var name = host.ToLowerInvariant().Trim('.');
        var labels = name.Split('.');
        var address = name.Contains(':') || labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit));
        if (isSuffix is null || address || labels.Length <= 1 || isSuffix(name)) return name;
        for (var count = labels.Length - 1; count >= 1; count--)
            if (isSuffix(string.Join('.', labels[^count..]))) return string.Join('.', labels[^(count + 1)..]);
        return name;
    }

    public static string Domain(string host) => Domain(host, Suffixes);

    /// Whether two hosts are one site: the same host, or the same registrable domain (idmsa.apple.com and
    /// account.apple.com). Never for an address, nor when the domain couldn't be worked out.
    public static bool SameSite(string one, string other, Func<string, bool>? isSuffix)
    {
        var a = one.ToLowerInvariant();
        var b = other.ToLowerInvariant();
        if (a == b) return true;
        var left = Domain(a, isSuffix);
        return left.Contains('.') && left == Domain(b, isSuffix);
    }

    public static bool SameSite(string one, string other) => SameSite(one, other, Suffixes);
}
