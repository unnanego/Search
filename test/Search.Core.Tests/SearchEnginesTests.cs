using Search.Core;

namespace Search.Core.Tests;

public class SearchEnginesTests
{
    [Fact]
    public void WordsAreEscapedIntoTheTemplate()
    {
        Assert.Equal("https://www.google.com/search?q=hello%20world", SearchEngine.Google.SearchUrl("  hello world ")?.AbsoluteUri);
        Assert.Equal("https://duckduckgo.com/?q=c%2B%2B%20%26%20%C3%BC", SearchEngine.DuckDuckGo.SearchUrl("c++ & ü")?.AbsoluteUri);
        Assert.Null(SearchEngine.Bing.SearchUrl("   "));
    }

    [Fact]
    public void CustomTemplatesAreTakenAsPeopleBringThem()
    {
        Assert.Equal("https://example.com/s?q=%s", SearchEngines.Written(" example.com/s?q={searchTerms} "));
        Assert.True(SearchEngines.Accepts("https://example.com/s?q=%s"));
        Assert.False(SearchEngines.Accepts("https://example.com/s"));
        Assert.False(SearchEngines.Accepts("ftp://example.com/s?q=%s"));
        Assert.False(SearchEngines.Accepts("https://%s.example.com/"));
        Assert.Equal("https://www.example.com/s?q=a%2Fb", SearchEngine.Custom.SearchUrl("a/b", "www.example.com/s?q={searchTerms}")?.AbsoluteUri);
        Assert.Equal("example.com", SearchEngine.Custom.Name("https://www.example.com/s?q=%s"));
    }

    [Fact]
    public void ACustomTemplateThatIsNoneSendsWordsToTheStandardEngine()
    {
        Assert.Equal(SearchEngines.Standard.Template(), SearchEngine.Custom.Template("not a template"));
        Assert.Equal("Google", SearchEngine.Custom.Name(""));
        Assert.Equal("Brave Search", SearchEngine.Brave.Name("ignored"));
    }

    [Fact]
    public void IdsAreTheMacsSettingsValues()
    {
        Assert.Equal(["google", "duckduckgo", "bing", "ecosia", "startpage", "kagi", "brave", "qwant", "custom"], SearchEngines.All.Select(e => e.Id()));
        Assert.Equal(SearchEngine.DuckDuckGo, SearchEngines.FromId("duckduckgo"));
        Assert.Null(SearchEngines.FromId("Google"));
    }
}
