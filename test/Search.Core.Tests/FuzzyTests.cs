using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class FuzzyTests
{
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[][] Words(string typed) => typed.Split(' ').Select(B).ToArray();

    [Theory]
    [InlineData("gitb", "github.com")]            // one letter wrong
    [InlineData("gthub", "github.com")]           // one missing
    [InlineData("giithub", "github.com")]         // one extra
    [InlineData("gtihub", "github.com")]          // two swapped
    [InlineData("igthub", "github.com")]          // the first two swapped
    [InlineData("hlelo", "say hello world")]      // a swap, in a later word
    [InlineData("concurensy", "swift guide concurrency")] // two, in a long word
    [InlineData("pipline", "git.example/team/api/-/pipelines")]
    public void ForgivesATypo(string typed, string text) => Assert.True(Fuzzy.Nearly(B(typed), text));

    [Theory]
    [InlineData("gti", "github.com")]             // three letters: never a guess
    [InlineData("xithub", "github.com")]          // the first letter wrong
    [InlineData("gxxhub", "github.com")]          // two typos in a short word
    [InlineData("conxxxxncy", "concurrency")]     // too many in a long one
    [InlineData("github", "")]
    public void DoesNotGuessWildly(string typed, string text) => Assert.False(Fuzzy.Nearly(B(typed), text));

    [Fact]
    public void MatchSaysWhetherATypoWasNeeded()
    {
        Assert.Equal(1, Fuzzy.Match(Words("guide swift"), "swift guide concurrency"));
        Assert.Equal(0, Fuzzy.Match(Words("guide concurency"), "swift guide concurrency"));
        Assert.Null(Fuzzy.Match(Words("guide kotlin"), "swift guide concurrency"));
    }

    [Fact]
    public void StartsWordOnlyAtTheStartOfAWord()
    {
        Assert.True(Fuzzy.StartsWord(B("api"), B("git.example/team/api")));
        Assert.False(Fuzzy.StartsWord(B("api"), B("rapid.example")));
        Assert.True(Fuzzy.StartsWord(B("api"), B("rapid api")));
        Assert.True(Fuzzy.StartsWord(B("/te"), B("git.example/team")));
        Assert.False(Fuzzy.StartsWord(B(""), B("anything")));
        Assert.True(Fuzzy.Contains(B("rapid"), B("api")));
    }

    [Fact]
    public void AbbreviatesTakesTheLettersInOrder()
    {
        Assert.True(Fuzzy.Abbreviates("dwn", "downloads"));
        Assert.False(Fuzzy.Abbreviates("dnw", "downloads"));
        Assert.False(Fuzzy.Abbreviates("wn", "downloads"));
        Assert.False(Fuzzy.Abbreviates("", "downloads"));
    }
}
