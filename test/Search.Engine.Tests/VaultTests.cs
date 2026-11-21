using System.Text;
using Search.Core;
using Search.Engine;

namespace Search.Engine.Tests;

/// Store pointed at a temp folder before anything reads it, so no test touches the real one.
internal static class TempStore
{
    static TempStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"search-engine-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Store.Root = root;
    }

    public static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}.dat";
}

public class VaultTests
{
    private readonly string _name = TempStore.Unique("passwords");

    [Fact]
    public async Task NothingOnDiskInTheClear()
    {
        var vault = new Vault(_name);
        Assert.True(vault.Save("example.com", "someone@mail.example", "correct horse battery"));
        await Vault.Flush();
        var bytes = await File.ReadAllBytesAsync(Store.File(_name));
        foreach (var clear in new[] { "correct horse", "someone@mail", "example.com" })
        {
            Assert.DoesNotContain(clear, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(clear, Encoding.Unicode.GetString(bytes));
        }
    }

    [Fact]
    public async Task ReadBackByAnotherVault()
    {
        var vault = new Vault(_name);
        vault.Save("example.com", "me", "pw", clear: true);
        vault.NeverFor("never.com");
        await Vault.Flush();
        var again = new Vault(_name);
        var kept = Assert.Single(again.All());
        Assert.Equal(("example.com", "me", true), (kept.Host, kept.User, kept.Clear));
        Assert.Equal("pw", again.Secret(kept));
        Assert.True(again.IsNever("never.com"));
    }

    [Fact]
    public void SameHostAndNameUpdatesRatherThanDuplicates()
    {
        var vault = new Vault(_name);
        var used = DateTimeOffset.UtcNow.AddDays(-3);
        vault.Save("example.com", "me", "old", used);
        var created = vault.All()[0].Created;
        vault.Save("example.com", "me", "new");
        var kept = Assert.Single(vault.All());
        Assert.Equal("new", vault.Secret(kept));
        Assert.Equal(created, kept.Created);
        Assert.Equal(used, kept.Used);
    }

    [Fact]
    public void UpdateOnlyWhatIsKept()
    {
        var vault = new Vault(_name);
        Assert.False(vault.Update(new Account("example.com", "me", default, null, false), "pw"));
        vault.Save("example.com", "me", "pw");
        Assert.True(vault.Update(vault.All()[0], "pw2"));
        Assert.Equal("pw2", vault.Login(vault.All()[0])!.Password);
    }

    [Fact]
    public void NoHostOrNoPasswordIsNotKept()
    {
        var vault = new Vault(_name);
        Assert.False(vault.Save("", "me", "pw"));
        Assert.False(vault.Save("example.com", "me", ""));
        Assert.Empty(vault.All());
    }

    [Fact]
    public void AnAccountWithNoNameIsKeptAndFound()
    {
        var vault = new Vault(_name);
        vault.Save("router.example.com", "", "pin");
        Assert.Equal("pin", vault.Secret(Assert.Single(vault.KeptFor("router.example.com"))));
    }

    [Fact]
    public void ForgetOneAndMany()
    {
        var vault = new Vault(_name);
        vault.Save("a.com", "x", "1"); vault.Save("b.com", "y", "2"); vault.Save("c.com", "z", "3");
        Assert.True(vault.Forget("a.com", "x"));
        Assert.False(vault.Forget("a.com", "x"));
        Assert.Equal(2, vault.Forget(vault.All()));
        Assert.Empty(vault.All());
    }

    [Fact]
    public void ConsiderMarksTheSameUsedAndAsksAboutTheRest()
    {
        var vault = new Vault(_name);
        vault.Save("example.com", "me", "pw");
        Assert.Equal(Saving.Known, vault.Consider("example.com", "me", "pw", clear: false));
        Assert.NotNull(vault.All()[0].Used);
        Assert.Equal(Saving.Changed, vault.Consider("example.com", "me", "pw2", clear: false));
        Assert.Equal(Saving.New, vault.Consider("example.com", "you", "pw", clear: false));
        Assert.Equal("pw", vault.Secret(vault.KeptFor("example.com").Single(a => a.User == "me")));
        vault.NeverFor("example.com");
        Assert.Equal(Saving.Known, vault.Consider("example.com", "you", "pw", clear: false));
        Assert.Single(vault.All());
    }

    [Fact]
    public void LastUsedIsTheNewest()
    {
        var vault = new Vault(_name);
        vault.Save("example.com", "old", "1", DateTimeOffset.UtcNow.AddDays(-2));
        vault.Save("example.com", "new", "2", DateTimeOffset.UtcNow.AddDays(-1));
        vault.Save("example.com", "never", "3");
        Assert.Equal("new", vault.LastUsed("example.com")!.User);
        vault.Touch(vault.KeptFor("example.com").Single(a => a.User == "old"));
        Assert.Equal("old", vault.LastUsed("example.com")!.User);
    }

    [Fact]
    public void OfferedIsThisHostsFirst()
    {
        var vault = new Vault(_name);
        vault.Save("example.com", "me", "pw");
        vault.Save("other.com", "me", "pw");
        Assert.Equal("example.com", Assert.Single(vault.Offered("example.com")).Host);
    }

    [Fact]
    public void ADamagedFileIsSetAsideNotWrittenOver()
    {
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File(_name), "not sealed");
        var vault = new Vault(_name);
        Assert.Empty(vault.All());
        Assert.False(File.Exists(Store.File(_name)));
        Assert.Contains(Directory.GetFiles(Store.Folder), f => Path.GetFileName(f).StartsWith(Path.GetFileNameWithoutExtension(_name) + ".unreadable-"));
    }

    [Fact]
    public void TakesAnExport()
    {
        var vault = new Vault(_name);
        Assert.Equal((1, 1), vault.Take("url,username,password\nhttps://www.example.com,me,pw\nandroid://x@com.app/,me,pw\n"));
        Assert.Equal("pw", vault.Secret(Assert.Single(vault.KeptFor("example.com"))));
    }

    [Fact] public void LoginNeverPrintsItsPassword() => Assert.DoesNotContain("hunter2", new Login("example.com", "me", "hunter2").ToString());
}
