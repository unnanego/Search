using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Search.Core;

// A bookmarks file, as every browser exports one (BookmarksFile.swift): Chrome, Edge, Safari, Firefox, Arc and the rest
// all write the same old Netscape format, an HTML page of nested lists. The one way in from a browser Search can't read
// directly, and, written back out, the one way to any of them.
//
//     <DT><H3>Folder</H3>
//     <DL><p>
//         <DT><A HREF="https://example.com/">Example</A>
//     </DL><p>
//
// Read loosely, as browsers write it loosely: tags in any case, paragraphs and closing tags left out, attributes in any
// order. Only http and https addresses are kept, as the other imports do.
public static partial class BookmarksFile
{
    /// Past this many folders deep, a list's pages join the folder that holds it.
    private const int Deepest = 64;

    /// The file's bookmarks, folders and all; null for a file that isn't one, or can't be read.
    public static List<Bookmark>? Read(string path, CancellationToken cancel = default)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var text = Text(data);
        return text.Contains("<DL", StringComparison.OrdinalIgnoreCase) ? Parse(text, cancel) : null;
    }

    /// UTF-8, or Latin-1 for an old file that isn't.
    private static string Text(byte[] data)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
    }

    /// One pass over the tags that matter. A folder's heading is kept until its list opens; a list that closes hands
    /// its pages to the folder that holds it.
    public static List<Bookmark> Parse(string text, CancellationToken cancel = default)
    {
        var stack = new List<(string? Title, List<Bookmark> Nodes)>();
        var root = new List<Bookmark>();
        string? heading = null;
        // The bookmarks bar, as Chrome and Firefox mark it: its pages go to the top, as bringing Chrome's in directly
        // puts them.
        var bar = false;

        void Append(Bookmark node) { if (stack.Count == 0) root.Add(node); else stack[^1].Nodes.Add(node); }
        void Close((string? Title, List<Bookmark> Nodes) list)
        {
            if (list.Title is { } title) Append(Bookmark.Folder(title, list.Nodes));
            else if (stack.Count == 0) root.AddRange(list.Nodes);
            else stack[^1].Nodes.AddRange(list.Nodes);
        }

        foreach (Match match in TagExpression().Matches(text))
        {
            cancel.ThrowIfCancellationRequested();
            if (match.Groups[1].Success)
            {
                var tag = match.Groups[1].Value.ToUpperInvariant();
                var attributes = match.Groups[2].Value;
                var title = Clean(match.Groups[3].Value);
                if (tag == "H3")
                {
                    heading = title;
                    bar = string.Equals(Attribute(ToolbarExpression(), attributes), "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (Attribute(HrefExpression(), attributes) is { } href && Uri.TryCreate(href, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                    Append(Bookmark.Site(title.Length == 0 ? (url.Host.Length > 0 ? url.Host : href) : title, url));
            }
            else if (match.Groups[4].Value.Length == 0)
            {
                // <DL>: the list of the folder just named, or, the first time, of the file itself. Past a depth no one
                // files at, a list's pages join the folder that holds it, so a file nested without end can't be too
                // deep to keep.
                if (stack.Count >= Deepest || (heading is null && stack.Count == 0 && root.Count == 0) || (bar && stack.Count <= 1)) stack.Add((null, []));
                else stack.Add((heading ?? "Folder", []));
                heading = null;
                bar = false;
            }
            else if (stack.Count > 0)
            {
                // </DL>
                var list = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                Close(list);
            }
        }
        // A file cut short: what was open still counts.
        while (stack.Count > 0)
        {
            var list = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Close(list);
        }
        return root;
    }

    // Writing. Not in the Swift, which only reads: the file as Chrome and Edge export it, the top level in the folder
    // they mark as the bookmarks bar (as the top level is Search's bar), so other browsers put it on theirs and this
    // reader puts it back at the top.

    public static string Export(IEnumerable<Bookmark> roots)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <!-- This is an automatically generated file.
                 It will be read and overwritten.
                 DO NOT EDIT! -->
            <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
            <TITLE>Bookmarks</TITLE>
            <H1>Bookmarks</H1>
            <DL><p>
                <DT><H3 PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
                <DL><p>

            """);
        Write(html, roots, 2);
        html.Append("    </DL><p>\n</DL><p>\n");
        return html.ToString();
    }

    private static void Write(StringBuilder html, IEnumerable<Bookmark> nodes, int depth)
    {
        var indent = new string(' ', depth * 4);
        foreach (var node in nodes)
        {
            if (node.Url is { } url) { html.Append($"{indent}<DT><A HREF=\"{Escape(url)}\">{Escape(node.Title)}</A>\n"); continue; }
            html.Append($"{indent}<DT><H3>{Escape(node.Title)}</H3>\n{indent}<DL><p>\n");
            Write(html, node.Children ?? [], depth + 1);
            html.Append($"{indent}</DL><p>\n");
        }
    }

    /// Only what would end the text or the attribute: anything else stays as it is, in UTF-8.
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // Reading's pieces.

    [GeneratedRegex(@"<(H3|A)\b([^>]*)>(.*?)</\1\s*>|<(/?)DL\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TagExpression();

    [GeneratedRegex("""HREF\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefExpression();

    [GeneratedRegex("""PERSONAL_TOOLBAR_FOLDER\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex ToolbarExpression();

    [GeneratedRegex("&#(x?)([0-9a-fA-F]+);")]
    private static partial Regex NumberedEntityExpression();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex MarkupExpression();

    private static string? Attribute(Regex expression, string text)
    {
        var match = expression.Match(text);
        if (!match.Success) return null;
        for (var group = 2; group <= 4; group++) if (match.Groups[group].Success) return Decode(match.Groups[group].Value);
        return null;
    }

    /// A title without any markup left in it, its entities decoded.
    private static string Clean(string text) => Decode(MarkupExpression().Replace(text, "")).Trim();

    private static readonly (string Entity, string Character)[] Named = [("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&#39;", "'"), ("&apos;", "'"), ("&nbsp;", " ")];

    private static string Decode(string text)
    {
        if (!text.Contains('&')) return text;
        var decoded = text;
        foreach (var (entity, character) in Named) decoded = decoded.Replace(entity, character, StringComparison.OrdinalIgnoreCase);
        // Numbered ones, then the ampersand itself last, so "&amp;lt;" stays "&lt;".
        decoded = NumberedEntityExpression().Replace(decoded, m =>
        {
            var hex = m.Groups[1].Value == "x";
            var digits = m.Groups[2].Value;
            var parsed = uint.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code);
            return parsed && code <= 0x10FFFF && Rune.IsValid((int)code) ? new Rune(code).ToString() : m.Value;
        });
        return decoded.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }
}
