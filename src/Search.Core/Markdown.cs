using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Search.Core;

/// A markdown page, drawn: its heading lifted out of the body, so reading mode can set it the way it sets every title.
public sealed record MarkdownPage(string? Title, string Html);

// Markdown, turned into the HTML reading mode shows (Markdown.swift; see Reader.twin). A site that publishes a
// markdown copy of a page has already decided what the article is; this only has to draw it. What it draws is
// CommonMark and the GitHub additions people actually write (tables, strikethrough, task lists, bare links), close
// enough rather than to the letter.
//
// Two departures, both because of where twins come from. Most are written in MDX, the docs-site dialect, which puts
// markdown inside components and indents it: so four spaces is not a code block here, and a component's tags are read
// for what they can give (a title, a link, a callout) and otherwise stepped over.
//
// Nothing from the markdown reaches the page as markup of its own. Every piece of text is escaped, every tag written in
// the source is rebuilt from a short list with its safe attributes only, and every address has to be a web or mail one.
public static class Markdown
{
    public static MarkdownPage Page(string text, Uri? baseUri)
    {
        var lines = Normalized(text);
        var title = FrontMatter(lines);
        var parser = new Parser(baseUri, References(lines));
        var blocks = parser.Blocks(lines);
        // The first heading of the top level is the title, if it comes before any section does. It may sit below a
        // skip link or a note to agents; it still names the page.
        if (blocks.FindIndex(b => b is Heading) is var first and >= 0 && blocks[first] is Heading { Level: 1 } heading)
        {
            title = Plain(heading.Text);
            blocks.RemoveAt(first);
        }
        return new MarkdownPage(string.IsNullOrEmpty(title) ? null : title, parser.Render(blocks, tight: false));
    }

    // MARK: lines

    private static List<string> Normalized(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ")
            // Inline uses these two to hold its place; nobody writes them.
            .Replace("", "").Replace("", "");
        if (text.StartsWith('﻿')) text = text[1..];
        return [.. text.Split('\n')];
    }

    /// YAML at the very top, between two lines of dashes: what the site publishes about the page. Only its title is of
    /// any use here.
    private static string? FrontMatter(List<string> lines)
    {
        if (lines.Count < 2 || lines[0].Trim() != "---" || !lines[1].Contains(':')) return null;
        var end = lines.FindIndex(1, l => l.Trim() is "---" or "...");
        if (end < 0) return null;
        var head = lines.GetRange(1, end - 1);
        lines.RemoveRange(0, end + 1);
        foreach (var line in head.Where(l => l.StartsWith("title:", StringComparison.Ordinal)))
        {
            var value = line["title:".Length..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0]) value = value[1..^1];
            return value;
        }
        return null;
    }

    /// `[name]: address "title"` lines, wherever they are, taken out of the text and kept for the links that name them.
    private static Dictionary<string, (string Address, string? Title)> References(List<string> lines)
    {
        var found = new Dictionary<string, (string, string?)>();
        var fenced = false;
        lines.RemoveAll(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) fenced = !fenced;
            var close = trimmed.IndexOf("]:", StringComparison.Ordinal);
            if (fenced || !trimmed.StartsWith('[') || close < 0) return false;
            var name = trimmed[1..close];
            if (name.Length == 0 || name.Contains(']')) return false;
            var scanner = new Scanner(trimmed[(close + 2)..]);
            if (scanner.Destination(null) is not { } destination) return false;
            found.TryAdd(Label(name), destination);
            return true;
        });
        return found;
    }

    public static string Label(string name) => string.Join(' ', name.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// Text with the markup taken off, for a title.
    internal static string Plain(string text) => Unescape(Regex.Replace(new Inline(null, new()).Render(text), "<[^>]+>", "")).Trim();

    // MARK: escaping

    public static string Escape(string text)
    {
        if (text.IndexOfAny(['&', '<', '>', '"', '\'']) < 0) return text;
        var out_ = new StringBuilder(text.Length + 16);
        foreach (var c in text)
            out_.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
        return out_.ToString();
    }

    internal static string Unescape(string text) =>
        !text.Contains('&') ? text : text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&");

    /// An address from the markdown, made whole against the twin's own and kept only if it goes somewhere a link should:
    /// a web page, a mail, or a place on this page. `javascript:` and its kind never get through. An address written
    /// whole is kept as written (Foundation's absoluteString), only spelled safely; a relative one is resolved.
    public static string? Address(string raw, Uri? baseUri)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        if (raw.StartsWith('#')) return raw;
        var written = Regex.Match(raw, "^[A-Za-z][A-Za-z0-9+.-]*:");
        if (written.Success)
        {
            if (written.Value[..^1].ToLowerInvariant() is not ("http" or "https" or "mailto")) return null;
            var spelled = Spelled(raw);
            return Uri.TryCreate(spelled, UriKind.Absolute, out _) ? spelled : null;
        }
        if (baseUri is null || !Uri.TryCreate(baseUri, Spelled(raw), out var url) || !url.IsAbsoluteUri) return null;
        return url.Scheme is "http" or "https" or "mailto" ? url.AbsoluteUri : null;
    }

    /// What a URL's fragment may hold, and # and %, kept; anything else percent-encoded, as Markdown.swift's fallback.
    private static string Spelled(string raw)
    {
        static bool Kept(char c) => char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/?#%".Contains(c);
        if (raw.All(Kept)) return raw;
        var out_ = new StringBuilder(raw.Length + 16);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in raw.EnumerateRunes())
        {
            if (rune.IsAscii && Kept((char)rune.Value)) { out_.Append((char)rune.Value); continue; }
            var count = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < count; i++) out_.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return out_.ToString();
    }

    /// The id a heading is given, so that a contents list's `#links` land. GitHub's way, which is what most of those
    /// links were written for.
    public static string Slug(string text)
    {
        var out_ = new StringBuilder();
        foreach (var rune in Plain(text).ToLowerInvariant().EnumerateRunes())
        {
            var kept = Rune.GetUnicodeCategory(rune) switch
            {
                >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.OtherNumber => true, // letters, marks and numbers
                _ => rune.Value is ' ' or '-' or '_'
            };
            if (kept) out_.Append(rune.ToString());
        }
        return out_.Replace(' ', '-').ToString();
    }

    // MARK: blocks

    private abstract record Block;
    private sealed record Heading(int Level, string Text) : Block;
    private sealed record Paragraph(string Text) : Block;
    private sealed record Code(string? Language, string Text) : Block;
    private sealed record Quote(List<Block> Blocks) : Block;
    private sealed record ListBlock(bool Ordered, int Start, List<List<Block>> Items, List<bool?> Checks, bool Tight) : Block;
    private sealed record Rule : Block;
    private sealed record Table(List<string> Head, List<string?> Aligns, List<List<string>> Rows) : Block;
    /// A line of tags written in the source, rebuilt through Inline.Tags.
    private sealed record Html(string Text) : Block;

    private readonly record struct Marker(int Indent, bool Ordered, int Start, char Sign, int Content);

    private static int Leading(string line) { var n = 0; while (n < line.Length && line[n] == ' ') n++; return n; }

    private sealed class Parser(Uri? baseUri, Dictionary<string, (string Address, string? Title)> references)
    {
        /// Ids already given, so two headings of the same name don't share one.
        private readonly Dictionary<string, int> _ids = new();

        // MARK: reading lines into blocks

        public List<Block> Blocks(List<string> lines)
        {
            var out_ = new List<Block>();
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.Length == 0) { i++; continue; }
                if (Fence(trimmed) is ({ } fence, { } info))
                {
                    var indent = Leading(line);
                    var body = new List<string>();
                    i++;
                    while (i < lines.Count)
                    {
                        var inner = lines[i];
                        var bare = inner.Trim();
                        if (bare.StartsWith(fence, StringComparison.Ordinal) && bare.All(c => c == fence[0])) { i++; break; }
                        // Code indented along with its fence comes back out with it.
                        body.Add(inner[Math.Min(indent, Leading(inner))..]);
                        i++;
                    }
                    var language = info.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    out_.Add(new Code(language, string.Join('\n', body)));
                    continue;
                }
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    while (i < lines.Count && !lines[i].Contains("-->")) i++;
                    i++;
                    continue;
                }
                if (Atx(trimmed) is { } heading) { out_.Add(heading); i++; continue; }
                if (IsRule(trimmed)) { out_.Add(new Rule()); i++; continue; }
                if (trimmed.StartsWith('>'))
                {
                    var inner = new List<string>();
                    while (i < lines.Count)
                    {
                        var bare = lines[i].Trim();
                        if (bare.StartsWith('>')) inner.Add(bare.Length > 1 && bare[1] == ' ' ? bare[2..] : bare[1..]);
                        // A paragraph carried on without its marker.
                        else if (bare.Length > 0 && inner.Count > 0 && inner[^1].Trim().Length > 0 && !Starts(bare)) inner.Add(bare);
                        else break;
                        i++;
                    }
                    // A note left at the top for agents (where to find the site's index for language models) says
                    // nothing to a person reading.
                    if (inner.Any(l => l.Contains("llms.txt"))) continue;
                    out_.Add(new Quote(Blocks(inner)));
                    continue;
                }
                if (MarkerOf(line) is not null)
                {
                    var (list, next) = ReadList(lines, i);
                    out_.Add(list);
                    i = next;
                    continue;
                }
                if (i + 1 < lines.Count && trimmed.Contains('|') && Delimiters(lines[i + 1]) is not null)
                {
                    var (table, next) = ReadTable(lines, i);
                    out_.Add(table);
                    i = next;
                    continue;
                }
                if (IsTagLine(trimmed))
                {
                    // A tag may be spread over lines, attribute by attribute.
                    var tag = trimmed;
                    var reach = 0;
                    while (!tag.Contains('>') && i + 1 < lines.Count && reach < 20)
                    {
                        var next = lines[i + 1].Trim();
                        if (next.Length == 0) break;
                        tag += " " + next;
                        i++;
                        reach++;
                    }
                    out_.Add(new Html(tag));
                    i++;
                    continue;
                }
                // A paragraph, until something else begins.
                var text = new List<string> { line.TrimStart(' ') };
                i++;
                int? setext = null;
                while (i < lines.Count)
                {
                    var next = lines[i];
                    var bare = next.Trim();
                    if (bare.Length == 0) break;
                    if (bare.All(c => c == '=')) { setext = 1; i++; break; }
                    if (bare.Length >= 2 && bare.All(c => c == '-')) { setext = 2; i++; break; }
                    if (Starts(bare) || Interrupts(next)) break;
                    if (bare.Contains('|') && i + 1 < lines.Count && Delimiters(lines[i + 1]) is not null) break;
                    text.Add(next.TrimStart(' '));
                    i++;
                }
                var joined = string.Join('\n', text);
                out_.Add(setext is { } level ? new Heading(level, joined.Trim()) : new Paragraph(joined));
            }
            return out_;
        }

        /// Lines that end a paragraph by starting something of their own.
        private static bool Starts(string bare) =>
            Fence(bare) is not null || Atx(bare) is not null || IsRule(bare) || bare.StartsWith('>') || bare.StartsWith("<!--", StringComparison.Ordinal) || IsTagLine(bare);

        /// A list may break into a paragraph too; an ordered one only if it counts from one, so that a sentence ending
        /// "in 1984." on a new line stays a sentence.
        private static bool Interrupts(string line) => MarkerOf(line) is { } item && (!item.Ordered || item.Start == 1);

        private static (string Fence, string Info)? Fence(string bare)
        {
            foreach (var mark in (string[])["```", "~~~"])
            {
                if (!bare.StartsWith(mark, StringComparison.Ordinal)) continue;
                var run = 0;
                while (run < bare.Length && bare[run] == mark[0]) run++;
                var info = bare[run..].Trim();
                if (mark == "```" && info.Contains('`')) return null;
                return (bare[..run], info);
            }
            return null;
        }

        private static Heading? Atx(string bare)
        {
            var hashes = 0;
            while (hashes < bare.Length && bare[hashes] == '#') hashes++;
            if (hashes is < 1 or > 6) return null;
            var rest = bare[hashes..];
            if (rest.Length > 0 && rest[0] != ' ') return null;
            var text = rest.Trim();
            // A closing run of #s is decoration, when it stands apart.
            if (Regex.Match(text, @"(^|\s)#+$") is { Success: true } closing) text = text[..closing.Index].Trim();
            return new Heading(hashes, text);
        }

        private static bool IsRule(string bare)
        {
            if (bare.Length == 0 || !"-*_".Contains(bare[0])) return false;
            var marks = bare.Replace(" ", "");
            return marks.Length >= 3 && marks.All(c => c == bare[0]);
        }

        /// A line that is nothing but tags: `<Note>`, `</Steps>`, `<img … />`, or the start of a tag that carries on
        /// below. An autolink is not one.
        private static bool IsTagLine(string bare)
        {
            if (!bare.StartsWith('<') || bare.Length <= 2) return false;
            if (!char.IsLetter(bare[1]) && bare[1] != '/') return false;
            if (Regex.IsMatch(bare, @"^<[A-Za-z][A-Za-z0-9+.-]*:[^\s<>]*>$")) return false;
            if (Regex.IsMatch(bare, @"^<[^\s<>@]+@[^\s<>@]+>$")) return false;
            return bare.EndsWith('>') || !bare.Contains('>');
        }

        private static Marker? MarkerOf(string line)
        {
            var indent = Leading(line);
            var rest = line[indent..];
            if (rest.Length == 0) return null;
            var first = rest[0];
            int width, start = 1;
            var ordered = false;
            var sign = first;
            if ("-*+".Contains(first)) { width = 1; rest = rest[1..]; }
            else
            {
                var digits = 0;
                while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
                if (digits is < 1 or > 9) return null;
                var number = int.Parse(rest[..digits], CultureInfo.InvariantCulture);
                rest = rest[digits..];
                if (rest.Length == 0 || rest[0] is not ('.' or ')')) return null;
                sign = rest[0];
                rest = rest[1..];
                ordered = true;
                start = number;
                width = digits + 1;
            }
            var spaces = Leading(rest);
            // A marker with nothing after it on the line is still an item; one run straight into the text ("-not",
            // "3.14") is not.
            if (spaces == 0 && rest.Length > 0) return null;
            width += spaces > 4 || rest.Length == spaces ? 1 : spaces;
            return new Marker(indent, ordered, start, sign, indent + width);
        }

        private (Block, int) ReadList(List<string> lines, int begin)
        {
            if (MarkerOf(lines[begin]) is not { } first) return (new Paragraph(lines[begin]), begin + 1);
            var items = new List<List<string>>();
            var current = new List<string>();
            var content = first.Content;
            bool blank = false, loose = false;
            var i = begin;
            while (i < lines.Count)
            {
                var line = lines[i];
                var bare = line.Trim();
                if (bare.Length == 0) { blank = true; current.Add(""); i++; continue; }
                var indent = Leading(line);
                if (MarkerOf(line) is { } item && item.Ordered == first.Ordered && item.Sign == first.Sign && indent < content && !IsRule(bare))
                {
                    if (items.Count > 0 || current.Count > 0)
                    {
                        if (blank) loose = true;
                        items.Add(current);
                    }
                    // The text begins after the marker; dropping the item's width from every line of it lines nested
                    // blocks up at 0.
                    current = [line[Math.Min(item.Content, line.Length)..]];
                    content = item.Content;
                    blank = false;
                    i++;
                    continue;
                }
                if (indent >= content)
                {
                    if (blank && current.Any(l => l.Length > 0)) loose = true;
                    current.Add(line[content..]);
                    blank = false;
                    i++;
                    continue;
                }
                // Carried on without its indent: part of the paragraph above, unless it starts something of its own.
                if (!blank && !Starts(bare) && MarkerOf(line) is null) { current.Add(bare); i++; continue; }
                break;
            }
            items.Add(current);
            // Blank lines at the very end belong to whatever follows.
            var parsed = new List<List<Block>>();
            var checks = new List<bool?>();
            foreach (var item in items)
            {
                while (item.Count > 0 && item[^1].Length == 0) item.RemoveAt(item.Count - 1);
                bool? check = null;
                if (item.Count > 0)
                {
                    var head = item[0];
                    foreach (var (box, state) in ((string, bool)[])[("[ ] ", false), ("[x] ", true), ("[X] ", true)])
                    {
                        if (!head.StartsWith(box, StringComparison.Ordinal)) continue;
                        check = state;
                        item[0] = head[box.Length..];
                    }
                }
                checks.Add(check);
                parsed.Add(Blocks(item));
            }
            return (new ListBlock(first.Ordered, first.Start, parsed, checks, !loose), i);
        }

        private static List<string?>? Delimiters(string line)
        {
            if (!line.Contains('|')) return null;
            var cells = Row(line);
            if (cells.Count == 0) return null;
            var aligns = new List<string?>();
            foreach (var cell in cells)
            {
                var bare = cell.Trim();
                if (!Regex.IsMatch(bare, "^:?-+:?$")) return null;
                aligns.Add((bare.StartsWith(':'), bare.EndsWith(':')) switch { (true, true) => "center", (false, true) => "right", (true, false) => "left", _ => null });
            }
            return aligns;
        }

        /// A table row's cells. A pipe inside code, or escaped, stays in its cell.
        private static List<string> Row(string line)
        {
            var bare = line.Trim();
            if (bare.StartsWith('|')) bare = bare[1..];
            if (bare.EndsWith('|') && !bare.EndsWith("\\|", StringComparison.Ordinal)) bare = bare[..^1];
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool code = false, escaped = false;
            foreach (var c in bare)
            {
                if (escaped)
                {
                    if (c != '|') cell.Append('\\');
                    cell.Append(c);
                    escaped = false;
                }
                else if (c == '\\') escaped = true;
                else if (c == '`') { code = !code; cell.Append(c); }
                else if (c == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
                else cell.Append(c);
            }
            if (escaped) cell.Append('\\');
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static (Block, int) ReadTable(List<string> lines, int begin)
        {
            var head = Row(lines[begin]);
            var aligns = Delimiters(lines[begin + 1]) ?? [];
            var rows = new List<List<string>>();
            var i = begin + 2;
            while (i < lines.Count)
            {
                var bare = lines[i].Trim();
                if (bare.Length == 0 || Starts(bare)) break;
                rows.Add(Row(bare));
                i++;
            }
            return (new Table(head, aligns, rows), i);
        }

        // MARK: drawing blocks

        /// A tag that stood aside draws nothing, and leaves no gap for it.
        public string Render(List<Block> blocks, bool tight) => string.Join('\n', blocks.Select(b => Render(b, tight)).Where(h => h.Length > 0));

        private string Render(Block block, bool tight)
        {
            switch (block)
            {
                case Heading(var level, var text):
                    var id = Slug(text);
                    if (_ids.TryGetValue(id, out var seen)) { _ids[id] = seen + 1; id += "-" + seen; }
                    else _ids[id] = 1;
                    var attribute = id.Length == 0 ? "" : $" id=\"{Escape(id)}\"";
                    return $"<h{level}{attribute}>{Spans(text)}</h{level}>";
                case Paragraph(var text):
                    var html = Spans(text);
                    if (html.Trim().Length == 0) return "";
                    return tight ? html : $"<p>{html}</p>";
                case Code(var language, var text):
                    var kind = language is null ? "" : $" class=\"language-{Escape(language)}\"";
                    return $"<pre><code{kind}>{Escape(text)}</code></pre>";
                case Quote(var blocks):
                    return $"<blockquote>{Render(blocks, tight: false)}</blockquote>";
                case ListBlock(var ordered, var start, var items, var checks, var tightList):
                    var tag = ordered ? "ol" : "ul";
                    var from = ordered && start != 1 ? $" start=\"{start}\"" : "";
                    var list = new StringBuilder($"<{tag}{from}>");
                    for (var n = 0; n < items.Count; n++)
                    {
                        var box = checks[n] switch { true => "<input type=\"checkbox\" checked disabled> ", false => "<input type=\"checkbox\" disabled> ", null => "" };
                        list.Append("<li>").Append(box).Append(Render(items[n], tightList)).Append("</li>");
                    }
                    return list.Append($"</{tag}>").ToString();
                case Rule:
                    return "<hr>";
                case Table(var head, var aligns, var rows):
                    string Cells(List<string> values, string cellTag) => string.Concat(aligns.Select((align, index) =>
                        $"<{cellTag}{(align is null ? "" : $" style=\"text-align:{align}\"")}>{Spans(index < values.Count ? values[index] : "")}</{cellTag}>"));
                    var body = string.Concat(rows.Select(r => $"<tr>{Cells(r, "td")}</tr>"));
                    // Wide tables scroll inside their own box, not the page.
                    return $"<div class=\"office-wide\"><table><thead><tr>{Cells(head, "th")}</tr></thead><tbody>{body}</tbody></table></div>";
                case Html(var text):
                    return Markdown.Inline.Tags(text, baseUri, standing: true);
                default:
                    return "";
            }
        }

        private string Spans(string text) => new Markdown.Inline(baseUri, references).Render(text);
    }

    // MARK: inline

    private sealed class Inline(Uri? baseUri, Dictionary<string, (string Address, string? Title)> references)
    {
        /// The parts that are already HTML (code, links, tags) stand aside as placeholders while emphasis is worked out
        /// over what is left, so that an asterisk inside a URL or a code span is never read as one.
        public string Render(string text)
        {
            var kept = new List<string>();
            string Keep(string html) { kept.Add(html); return $"{kept.Count - 1}"; }

            var scanner = new Scanner(text);
            var out_ = new StringBuilder();
            var pending = new StringBuilder();
            void Flush() { out_.Append(Escape(pending.ToString())); pending.Clear(); }

            while (scanner.Peek() is { } c)
            {
                switch (c)
                {
                    case '\\':
                        scanner.Advance();
                        if (scanner.Peek() is { } next && char.IsAscii(next) && (char.IsPunctuation(next) || char.IsSymbol(next)))
                        {
                            // Held aside, so the emphasis pass never sees it as a mark.
                            Flush();
                            out_.Append(Keep(Escape(next.ToString())));
                            scanner.Advance();
                        }
                        else if (scanner.Peek() == '\n') { Flush(); out_.Append(Keep("<br>")); }
                        else pending.Append('\\');
                        break;
                    case '`':
                        var mark = scanner.Run('`');
                        if (scanner.Until(mark) is { } found)
                        {
                            var code = found.Replace('\n', ' ');
                            if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Any(ch => ch != ' ')) code = code[1..^1];
                            Flush();
                            out_.Append(Keep($"<code>{Escape(code)}</code>"));
                        }
                        else pending.Append(mark);
                        break;
                    case '!' when scanner.Peek(1) == '[':
                        scanner.Advance();
                        if (Link(ref scanner) is ({ } alt, { } address, var title))
                        {
                            Flush();
                            // A picture with nowhere to come from (`![]()`) is left out.
                            if (Address(address, baseUri) is { } source)
                            {
                                var caption = title is null ? "" : $" title=\"{Escape(title)}\"";
                                out_.Append(Keep($"<img src=\"{Escape(source)}\" alt=\"{Escape(Plain(alt))}\"{caption} loading=\"lazy\">"));
                            }
                        }
                        // Not a picture after all; the bracket is read next time round.
                        else pending.Append('!');
                        break;
                    case '[':
                        var save = scanner.Position;
                        if (Link(ref scanner) is ({ } label, { } target, var linkTitle))
                        {
                            Flush();
                            var inner = new Inline(baseUri, references).Render(label);
                            if (Address(target, baseUri) is { } href)
                            {
                                var caption = linkTitle is null ? "" : $" title=\"{Escape(linkTitle)}\"";
                                out_.Append(Keep($"<a href=\"{Escape(href)}\"{caption}>{inner}</a>"));
                            }
                            else out_.Append(Keep(inner));
                        }
                        else { scanner.Position = save + 1; pending.Append('['); }
                        break;
                    case '<':
                        if (Angle(scanner.Ahead(2000)) is ({ } tagged, var length))
                        {
                            Flush();
                            out_.Append(Keep(tagged));
                            scanner.Position += length;
                        }
                        else { pending.Append('<'); scanner.Advance(); }
                        break;
                    case 'h' or 'w':
                        var apart = pending.Length == 0 || !char.IsLetterOrDigit(pending[^1]);
                        var rest = scanner.Ahead(2000);
                        var looks = rest.StartsWith("http", StringComparison.Ordinal) || rest.StartsWith("www.", StringComparison.Ordinal);
                        if (apart && looks && Bare(rest) is ({ } linked, var span))
                        {
                            Flush();
                            out_.Append(Keep(linked));
                            scanner.Position += span;
                        }
                        else { pending.Append(c); scanner.Advance(); }
                        break;
                    case '\n':
                        // Two spaces at the end of a line break it.
                        var hard = pending.Length >= 2 && pending[^1] == ' ' && pending[^2] == ' ';
                        while (pending.Length > 0 && pending[^1] == ' ') pending.Length--;
                        if (hard) { Flush(); out_.Append(Keep("<br>")); }
                        else pending.Append('\n');
                        scanner.Advance();
                        break;
                    case '&':
                        // An entity written into the text is already HTML: &amp;, &copy;, &#8212; go through as they are.
                        var window = scanner.Ahead(12);
                        if (window.IndexOf(';') is var end and >= 0 && window[..(end + 1)] is var entity && Regex.IsMatch(entity, "^&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,10});$"))
                        {
                            Flush();
                            out_.Append(Keep(entity));
                            scanner.Position += entity.Length;
                        }
                        else { pending.Append('&'); scanner.Advance(); }
                        break;
                    default:
                        pending.Append(c);
                        scanner.Advance();
                        break;
                }
            }
            Flush();

            var result = Emphasis(out_.ToString());
            // Placeholders can hold placeholders of their own (a link's text).
            for (var index = kept.Count - 1; index >= 0; index--) result = result.Replace($"{index}", kept[index]);
            return result;
        }

        private static readonly (Regex Pattern, string Template)[] _emphases =
        [
            (new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.Singleline), "<strong>$1</strong>"),
            (new(@"(?<![\p{L}\p{N}_])__(?=\S)(.+?)(?<=\S)__(?![\p{L}\p{N}_])", RegexOptions.Singleline), "<strong>$1</strong>"),
            (new(@"\*(?=[^\s*])(.+?)(?<=[^\s*])\*", RegexOptions.Singleline), "<em>$1</em>"),
            (new(@"(?<![\p{L}\p{N}_])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![\p{L}\p{N}_])", RegexOptions.Singleline), "<em>$1</em>"),
            (new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline), "<del>$1</del>"),
        ];

        private static string Emphasis(string text)
        {
            if (text.IndexOfAny(['*', '_', '~']) < 0) return text;
            foreach (var (pattern, template) in _emphases) text = pattern.Replace(text, template);
            return text;
        }

        /// `[text](address "title")`, `[text][name]`, `[text][]` or `[name]`, with the scanner on the opening bracket.
        /// Answers the link's text, where it goes and its title, and leaves the scanner after it.
        private (string Label, string Address, string? Title)? Link(ref Scanner scanner)
        {
            var save = scanner.Position;
            if (scanner.Peek() != '[') return null;
            scanner.Advance();
            var depth = 1;
            var label = new StringBuilder();
            while (scanner.Peek() is { } c)
            {
                scanner.Advance();
                if (c == '\\' && scanner.Peek() is { } escaped) { label.Append(c).Append(escaped); scanner.Advance(); continue; }
                if (c == '`')
                {
                    // A bracket inside code doesn't close the text.
                    label.Append(c);
                    while (scanner.Peek() is { } inner)
                    {
                        scanner.Advance();
                        label.Append(inner);
                        if (inner == '`') break;
                    }
                    continue;
                }
                if (c == '[') depth++;
                if (c == ']' && --depth == 0) break;
                label.Append(c);
            }
            if (depth != 0) { scanner.Position = save; return null; }
            if (scanner.Peek() == '(')
            {
                scanner.Advance();
                if (scanner.Destination(')') is ({ } address, var title)) return (label.ToString(), address, title);
                scanner.Position = save;
                return null;
            }
            var name = label.ToString();
            if (scanner.Peek() == '[')
            {
                var back = scanner.Position;
                scanner.Advance();
                var written = new StringBuilder();
                while (scanner.Peek() is { } c && c != ']' && c != '[') { written.Append(c); scanner.Advance(); }
                if (scanner.Peek() == ']')
                {
                    scanner.Advance();
                    if (written.Length > 0) name = written.ToString();
                }
                else scanner.Position = back;
            }
            if (references.TryGetValue(Label(name), out var reference)) return (label.ToString(), reference.Address, reference.Title);
            scanner.Position = save;
            return null;
        }

        /// Something in angle brackets: an autolink, a comment, or a tag.
        private (string Html, int Length)? Angle(string rest)
        {
            var text = rest[..Math.Min(2000, rest.Length)];
            if (Regex.Match(text, @"^<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>") is { Success: true } link)
            {
                var inner = link.Value[1..^1];
                if (Address(inner, baseUri) is not { } target) return (Escape(inner), link.Length);
                return ($"<a href=\"{Escape(target)}\">{Escape(inner)}</a>", link.Length);
            }
            if (Regex.Match(text, @"^<[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+>") is { Success: true } mail)
            {
                var inner = mail.Value[1..^1];
                return ($"<a href=\"mailto:{Escape(inner)}\">{Escape(inner)}</a>", mail.Length);
            }
            if (text.StartsWith("<!--", StringComparison.Ordinal) && text.IndexOf("-->", StringComparison.Ordinal) is var end and >= 0) return ("", end + 3);
            if (_tagAnchored.Match(text) is { Success: true } tag) return (Tags(tag.Value, baseUri, standing: false), tag.Length);
            return null;
        }

        /// A web address written out bare, as GitHub links it: from http(s):// or www. to the next space, less the
        /// punctuation that ends a sentence.
        private static (string Html, int Length)? Bare(string rest)
        {
            var text = rest[..Math.Min(2000, rest.Length)];
            if (Regex.Match(text, @"^(https?://|www\.)[^\s<>]+") is not { Success: true } match) return null;
            var found = match.Value;
            while (found.Length > 0 && (".,:;!?\"'*_~".Contains(found[^1]) || (found[^1] == ')' && found.Count(c => c == '(') < found.Count(c => c == ')')))) found = found[..^1];
            if (found.Length <= 7) return null;
            var raw = found.StartsWith("www.", StringComparison.Ordinal) ? "https://" + found : found;
            if (Address(raw, null) is not { } target) return null;
            return ($"<a href=\"{Escape(target)}\">{Escape(found)}</a>", found.Length);
        }

        // MARK: tags written in the markdown

        private const string TagPattern = @"</?[A-Za-z][A-Za-z0-9-]*(\s+[^\s=>/""']+(\s*=\s*(""[^""]*""|'[^']*'|\{[^}]*\}|[^\s""'=<>`]+))?)*\s*/?>";
        private static readonly Regex _tag = new(TagPattern);
        private static readonly Regex _tagAnchored = new("^" + TagPattern);

        /// Tags drawn as themselves, with the attributes each may keep.
        private static readonly Dictionary<string, string[]> _kept = new()
        {
            ["a"] = ["href", "title"], ["img"] = ["alt", "src", "title"], ["br"] = [],
            ["em"] = [], ["i"] = [], ["strong"] = [], ["b"] = [], ["u"] = [], ["s"] = [], ["del"] = [], ["ins"] = [],
            ["code"] = [], ["kbd"] = [], ["mark"] = [], ["sub"] = [], ["sup"] = [], ["small"] = [], ["abbr"] = ["title"],
            ["details"] = [], ["summary"] = [], ["figure"] = [], ["figcaption"] = [],
            ["table"] = [], ["thead"] = [], ["tbody"] = [], ["tr"] = [], ["th"] = ["align"], ["td"] = ["align"],
            ["blockquote"] = [], ["hr"] = [], ["p"] = [], ["pre"] = [],
            ["ul"] = [], ["ol"] = ["start"], ["li"] = [],
            ["h1"] = [], ["h2"] = [], ["h3"] = [], ["h4"] = [], ["h5"] = [], ["h6"] = [],
        };

        /// The callouts docs sites write as components (a note, a tip, a warning), drawn as a quote, which is what they
        /// are to a reader.
        private static readonly HashSet<string> _callouts = ["note", "tip", "info", "warning", "caution", "danger", "check", "callout", "aside", "admonition"];

        /// Tags from the source, rebuilt: a known one with its safe attributes, a callout as a quote, and any other
        /// component reduced to its title (linked, if it goes somewhere) or to nothing. Standing tags are on a line of
        /// their own, where a title can be a paragraph.
        public static string Tags(string text, Uri? baseUri, bool standing)
        {
            var out_ = new StringBuilder();
            var last = 0;
            foreach (Match match in _tag.Matches(text))
            {
                out_.Append(Escape(text[last..match.Index]));
                last = match.Index + match.Length;
                out_.Append(Tag(match.Value, baseUri, standing));
            }
            return out_.Append(Escape(text[last..])).ToString();
        }

        private static string Tag(string source, Uri? baseUri, bool standing)
        {
            var closing = source.StartsWith("</", StringComparison.Ordinal);
            var from = closing ? 2 : 1;
            var to = from;
            while (to < source.Length && (char.IsLetterOrDigit(source[to]) || source[to] == '-')) to++;
            var name = source[from..to];
            var lower = name.ToLowerInvariant();
            if (_callouts.Contains(lower)) return closing ? "</blockquote>" : "<blockquote>";
            var attributes = closing ? new Dictionary<string, string>() : Attributes(source);
            // Lowercase is HTML; a capital is a component. A <Table> is not a <table>.
            if (name != lower || !_kept.TryGetValue(lower, out var allowed))
            {
                if (closing || !attributes.TryGetValue("title", out var title) || title.Length == 0) return "";
                var html = $"<strong>{Escape(title)}</strong>";
                if (attributes.TryGetValue("href", out var href) && Address(href, baseUri) is { } target) html = $"<a href=\"{Escape(target)}\">{html}</a>";
                return standing ? $"<p class=\"office-part\">{html}</p>" : html;
            }
            if (closing) return $"</{lower}>";
            var built = new StringBuilder("<").Append(lower);
            // In order of name, as the Swift's sorted set: alt before src before title.
            foreach (var key in allowed)
            {
                if (!attributes.TryGetValue(key, out var value)) continue;
                if (key is "href" or "src")
                {
                    if (Address(value, baseUri) is not { } target) { if (key == "src") return ""; continue; }
                    value = target;
                }
                built.Append($" {key}=\"{Escape(value)}\"");
            }
            if (lower == "img")
            {
                if (!attributes.ContainsKey("src")) return "";
                built.Append(" loading=\"lazy\"");
            }
            return built.Append('>').ToString();
        }

        private static readonly Regex _attribute = new(@"([^\s=>/""']+)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s""'=<>`{}]+))");

        private static Dictionary<string, string> Attributes(string source)
        {
            var found = new Dictionary<string, string>();
            foreach (Match match in _attribute.Matches(source))
            {
                var value = new[] { 3, 4, 5 }.Select(g => match.Groups[g]).FirstOrDefault(g => g.Success);
                found[match.Groups[1].Value.ToLowerInvariant()] = value is null ? "" : Unescape(value.Value);
            }
            return found;
        }
    }

    // MARK: scanning

    private struct Scanner(string characters)
    {
        private readonly string _characters = characters;
        public int Position;

        /// What comes next, up to `count` characters of it.
        public readonly string Ahead(int count) => _characters.Substring(Position, Math.Min(count, _characters.Length - Position));

        public readonly char? Peek(int ahead = 0) => Position + ahead < _characters.Length ? _characters[Position + ahead] : null;

        public void Advance() => Position++;

        public string Run(char c)
        {
            var from = Position;
            while (Peek() == c) Advance();
            return _characters[from..Position];
        }

        /// The text up to a run of backticks exactly as long as `mark`, with the scanner left after it; nothing, and the
        /// scanner unmoved, if there isn't one.
        public string? Until(string mark)
        {
            var save = Position;
            var text = new StringBuilder();
            while (Peek() is { } c)
            {
                if (c == '`')
                {
                    var run = Run('`');
                    if (run.Length == mark.Length) return text.ToString();
                    text.Append(run);
                }
                else { text.Append(c); Advance(); }
            }
            Position = save;
            return null;
        }

        /// `address "title")` after a link's opening parenthesis, or the same with no closing one for a reference
        /// definition.
        public (string Address, string? Title)? Destination(char? closing)
        {
            SkipSpaces();
            var address = new StringBuilder();
            if (Peek() == '<')
            {
                Advance();
                while (Peek() is { } c && c != '>' && c != '\n') { address.Append(c); Advance(); }
                if (Peek() != '>') return null;
                Advance();
            }
            else
            {
                var depth = 0;
                while (Peek() is { } c && !char.IsWhiteSpace(c))
                {
                    if (c == '(') depth++;
                    if (c == ')')
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    if (c == '\\' && Peek(1) is { } next && char.IsPunctuation(next)) { address.Append(next); Position += 2; continue; }
                    address.Append(c);
                    Advance();
                }
            }
            SkipSpaces();
            string? title = null;
            if (Peek() is { } open && open is '"' or '\'' or '(')
            {
                var close = open == '(' ? ')' : open;
                Advance();
                var text = new StringBuilder();
                while (Peek() is { } c && c != close) { text.Append(c); Advance(); }
                if (Peek() != close) return null;
                Advance();
                title = text.ToString();
                SkipSpaces();
            }
            if (closing is { } end)
            {
                if (Peek() != end) return null;
                Advance();
            }
            else if (Position < _characters.Length) return null;
            return (address.ToString(), title);
        }

        public void SkipSpaces() { while (Peek() is ' ' or '\n') Advance(); }
    }
}
