using Search.Core;

namespace Search.Core.Tests;

public class RegistrableTests
{
    private static readonly HashSet<string> _list = ["com", "uk", "co.uk", "github.io"];
    private static bool Suffix(string name) => _list.Contains(name);

    [Fact] public void SubdomainBelongsToItsDomain() => Assert.Equal("apple.com", Registrable.Domain("idmsa.apple.com", Suffix));
    [Fact] public void TwoLabelSuffix() => Assert.Equal("bbc.co.uk", Registrable.Domain("www.bbc.co.uk", Suffix));
    [Fact] public void PrivateSuffixKeepsSitesApart() => Assert.False(Registrable.SameSite("alice.github.io", "bob.github.io", Suffix));
    [Fact] public void SameDomainIsOneSite() => Assert.True(Registrable.SameSite("idmsa.apple.com", "account.apple.com", Suffix));
    [Fact] public void AddressesAreOnlyThemselves() => Assert.False(Registrable.SameSite("10.0.0.1", "10.0.0.2", Suffix));
    [Fact] public void WithoutAListOnlyTheSameHost() { Assert.False(Registrable.SameSite("a.apple.com", "b.apple.com", null)); Assert.True(Registrable.SameSite("Apple.com", "apple.com", null)); }
    [Fact] public void SuffixItselfIsItsOwnDomain() => Assert.Equal("co.uk", Registrable.Domain("co.uk", Suffix));
}
