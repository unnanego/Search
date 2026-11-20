namespace Search.Core;

// The engine words go to when what was typed isn't a place (Engine.swift), and a custom one's address as people
// bring it: copied from another browser, or typed without its https://.
public enum SearchEngine { Google, DuckDuckGo, Bing, Ecosia, Startpage, Kagi, Brave, Qwant, Custom }

public static class SearchEngines
{
    public const SearchEngine Standard = SearchEngine.Google;

    private const string Mark = "SEARCHWORDSGOHERE";

    public static IReadOnlyList<SearchEngine> All { get; } = Enum.GetValues<SearchEngine>();

    /// The name settings keep it by, the same as the Mac's.
    public static string Id(this SearchEngine engine) => engine.ToString().ToLowerInvariant();

    public static SearchEngine? FromId(string id)
    {
        foreach (var engine in All) if (engine.Id() == id) return engine;
        return null;
    }

    public static string Title(this SearchEngine engine) => engine switch
    {
        SearchEngine.Google => "Google",
        SearchEngine.DuckDuckGo => "DuckDuckGo",
        SearchEngine.Bing => "Bing",
        SearchEngine.Ecosia => "Ecosia",
        SearchEngine.Startpage => "Startpage",
        SearchEngine.Kagi => "Kagi",
        SearchEngine.Brave => "Brave Search",
        SearchEngine.Qwant => "Qwant",
        _ => "Custom",
    };

    /// Where the words go, %s standing for them. A custom template that isn't one sends them to the standard engine.
    public static string Template(this SearchEngine engine, string custom = "") => engine switch
    {
        SearchEngine.Google => "https://www.google.com/search?q=%s",
        SearchEngine.DuckDuckGo => "https://duckduckgo.com/?q=%s",
        SearchEngine.Bing => "https://www.bing.com/search?q=%s",
        SearchEngine.Ecosia => "https://www.ecosia.org/search?q=%s",
        SearchEngine.Startpage => "https://www.startpage.com/sp/search?query=%s",
        SearchEngine.Kagi => "https://kagi.com/search?q=%s",
        SearchEngine.Brave => "https://search.brave.com/search?q=%s",
        SearchEngine.Qwant => "https://www.qwant.com/?q=%s",
        _ => Accepts(Written(custom)) ? Written(custom) : Standard.Template(),
    };

    public static string Name(this SearchEngine engine, string custom = "") =>
        engine != SearchEngine.Custom ? engine.Title() : BareHost(Written(custom)) ?? Standard.Title();

    /// The words as a search: null when there are none.
    public static Uri? SearchUrl(this SearchEngine engine, string words, string custom = "") => Url(words, engine.Template(custom));

    /// A custom engine's address as people bring it: copied from another browser with {searchTerms} where the words
    /// go, or typed without its https://. Either one was turned away, and every search went to Google.
    public static string Written(string template)
    {
        var text = template.Trim().Replace("{searchTerms}", "%s");
        return text.Length > 0 && !text.Contains("://") ? "https://" + text : text;
    }

    public static bool Accepts(string template) => Host(template) is not null;

    /// A template's host without any leading "www.": what a person would call the site if you asked them.
    public static string? BareHost(string template) => Host(template) is { } host ? host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host : null;

    public static Uri? Url(string text, string template)
    {
        var words = text.Trim();
        if (words.Length == 0 || !Uri.TryCreate(template.Replace("%s", Mark), UriKind.Absolute, out var marked)) return null;
        return Uri.TryCreate(marked.AbsoluteUri.Replace(Mark, Uri.EscapeDataString(words)), UriKind.Absolute, out var url) ? url : null;
    }

    private static string? Host(string template)
    {
        var trimmed = template.Trim();
        if (!trimmed.Contains("%s") || !Uri.TryCreate(trimmed.Replace("%s", "a"), UriKind.Absolute, out var parts)
            || !Uri.TryCreate(trimmed.Replace("%s", "b"), UriKind.Absolute, out var other)) return null;
        if (parts.Scheme is not ("http" or "https") || !Address.HasHost(parts) || parts.Host != other.Host) return null;
        return parts.Host.ToLowerInvariant();
    }
}
