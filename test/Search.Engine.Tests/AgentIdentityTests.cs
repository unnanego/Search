using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Search.Core;
using Search.Engine;

namespace Search.Engine.Tests;

/// Who is at the other end of the agent's pipe and what started it (AgentIdentity), the switch's mark and the sealed
/// file the remembered agents are kept in.
public class AgentIdentityTests
{
    private readonly string _name = TempStore.Unique("agents");

    [Fact]
    public async Task TheClientIsFoundByThePipeAndRunsAsYou()
    {
        var name = $"SearchIdentityTests-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        using var peer = AgentIdentity.Peer(server.SafePipeHandle);
        Assert.NotNull(peer);
        Assert.Equal(Environment.ProcessId, peer.Pid);
        Assert.True(AgentIdentity.Alive(peer.Process));
        Assert.StartsWith(Path.GetFileName(Environment.ProcessPath!), AgentIdentity.Program(peer), StringComparison.OrdinalIgnoreCase);
        // What started this test run is no agent's own program: examined, and never offered "Always allow".
        var seen = AgentIdentity.Examine(peer, new[] { "claude.exe signed by Anthropic, PBC" });
        Assert.NotNull(seen);
        Assert.NotNull(seen.Value.Launcher.Unrememberable);
        Assert.Empty(seen.Value.Passed);
    }

    [Fact]
    public void AParentStartedAfterItsChildIsNoParent()
    {
        using var self = Process.GetCurrentProcess();
        var parent = AgentIdentity.Parent(self.SafeHandle);
        Assert.NotNull(parent);
        Assert.True(AgentIdentity.Created(self.SafeHandle) > 0);
    }

    [Fact]
    public void SignaturesAreReadOfflineWithTheirSigner()
    {
        var runtime = typeof(object).Assembly.Location;
        var (signed, signer) = AgentIdentity.Signature(runtime);
        Assert.True(signed, runtime);
        Assert.True(signer is ".NET" || signer.Contains("Microsoft"), signer);
        var junk = Path.Combine(Path.GetTempPath(), $"search-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("MZ not really a program"));
        try { Assert.Equal((false, ""), AgentIdentity.Signature(junk)); }
        finally { File.Delete(junk); }
        Assert.Equal((false, ""), AgentIdentity.Signature(@"C:\nowhere\at\all.exe"));
    }

    [Fact]
    public void TheMarkIsThisWorldsAndSealed()
    {
        var mark = new SwitchMark(_name);
        Assert.False(mark.Given);
        Assert.True(mark.Grant());
        Assert.True(mark.Given);
        var file = Store.File(_name + ".mark");
        Assert.DoesNotContain("switched on", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        // Written by some other program, in the clear: no mark.
        File.WriteAllText(file, $"{_name} switched on in {Store.Folder}");
        Assert.False(mark.Given);
        mark.Grant();
        mark.Revoke();
        Assert.False(mark.Given);
    }

    [Fact]
    public void RememberedAgentsAreSealedOnDisk()
    {
        var vault = new SealedVault(_name + ".trusted", "Search tests trusted agents");
        var trust = new AgentTrust(vault);
        Assert.True(trust.Remember("Claude Code", new AgentLauncher { Path = @"C:\x\claude.exe", Signed = true, Signer = "Anthropic, PBC" }));
        Assert.DoesNotContain("Claude", Encoding.UTF8.GetString(File.ReadAllBytes(Store.File(_name + ".trusted"))));
        Assert.Equal("Claude Code", Assert.Single(new AgentTrust(vault).Remembered).Client);
        // Sealed for another purpose, or by hand: nobody.
        Assert.Empty(new AgentTrust(new SealedVault(_name + ".trusted", "something else")).Remembered);
        AgentSwitch.Turned(false, trust);
        Assert.Empty(new AgentTrust(vault).Remembered);
    }
}
