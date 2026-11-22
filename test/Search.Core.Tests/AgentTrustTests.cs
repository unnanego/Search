using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// "Always allow" for a coding agent (AgentTrust): what is remembered is the signature of the program that started
/// the client, never the name it gives; checked again on every connection (AgentTrustTests.swift's, for Windows).
public class AgentTrustTests
{
    /// For the tests: somewhere in memory.
    private sealed class MemoryVault(byte[]? data = null) : IAgentVault
    {
        public byte[]? Data { get; private set; } = data;
        public bool Usable { get; set; } = true;
        /// Every write fails, as a disk might.
        public bool Refuses { get; set; }
        public byte[]? Read() => Usable ? Data : null;
        public bool Write(byte[] data) { if (!Usable || Refuses) return false; Data = data; return true; }
        public void Erase() => Data = null;
    }

    private static readonly string Claude = AgentLauncher.Requirement("claude.exe", "Anthropic, PBC");

    /// Claude Code's own, as its signature would say.
    private static AgentLauncher Signed(string path = @"C:\Users\someone\.local\bin\claude.exe", string signer = "Anthropic, PBC", bool signed = true) => new() { Path = path, Signer = signer, Signed = signed };

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void ClaudeCodeSignedByAnthropicCanBeRemembered()
    {
        Assert.Null(Signed().Unrememberable);
        Assert.Equal("claude.exe signed by Anthropic, PBC", Signed().Allowlisted);
        Assert.Null(Signed(@"D:\Tools\CLAUDE.EXE").Unrememberable);
        Assert.True(Signed().Passes(Claude));
    }

    [Theory]
    [InlineData(@"C:\Python313\python.exe", "Python Software Foundation", true, "isn't an agent's own program")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "Microsoft Windows", true, "isn't an agent's own program")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "Microsoft Corporation", true, "isn't an agent's own program")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Microsoft Corporation", true, "isn't an agent's own program")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", "Microsoft Corporation", true, "isn't an agent's own program")]
    [InlineData(@"C:\Temp\claude.exe", "Someone Else", true, "isn't an agent's own program")]
    [InlineData(@"C:\Temp\claude.exe", "Anthropic, PBC", false, "isn't signed")]
    [InlineData(@"C:\Temp\claude.exe", "", true, "isn't signed by a named developer")]
    [InlineData("", "", false, "the program that started it, which started it, isn't signed")]
    public void UnsignedOrSharedLaunchersAreNeverOffered(string path, string signer, bool signed, string why)
    {
        var launcher = Signed(path, signer, signed);
        Assert.Contains(why, launcher.Unrememberable);
        Assert.Null(launcher.Allowlisted);
        Assert.False(new AgentTrust(new MemoryVault()).Remember("Claude Code", launcher));
    }

    [Fact]
    public void ARequirementIsPassedOnlyByItsNameAndSigner()
    {
        Assert.False(Signed(signer: "Anthropic PBC").Passes(Claude));
        Assert.False(Signed(@"C:\x\node.exe").Passes(Claude));
        Assert.False(Signed(signed: false).Passes(Claude));
        Assert.False(Signed().Passes(""));
    }

    [Fact]
    public void RememberedIsLetInOnlyWithItsNameAndItsSignature()
    {
        var trust = new AgentTrust(new MemoryVault());
        Assert.True(trust.Remember("Claude Code", Signed()));
        Assert.NotNull(AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, Now));
        Assert.Null(AgentTrust.Match("Codex", Signed(), [Claude], trust.Remembered, testing: false, Now));
        Assert.Null(AgentTrust.Match("Claude Code", Signed(), [], trust.Remembered, testing: false, Now));
        Assert.Null(AgentTrust.Match("Claude Code", null, [Claude], trust.Remembered, testing: false, Now));
        Assert.Null(AgentTrust.Match("Claude Code", Signed(@"C:\Python313\python.exe", "Python Software Foundation"), [Claude], trust.Remembered, testing: false, Now));
        Assert.Equal([Claude], trust.Requirements("Claude Code"));
        Assert.Empty(trust.Requirements("Codex"));
        Assert.Equal(@"C:\Users\someone\.local\bin\claude.exe", trust.Remembered[0].Path);
    }

    [Fact]
    public void ForgottenIsAskedAgain()
    {
        var vault = new MemoryVault();
        var trust = new AgentTrust(vault);
        trust.Remember("Claude Code", Signed());
        trust.Remember("Other", Signed());
        Assert.Equal(2, trust.Remembered.Count);
        trust.Forget(trust.Remembered[0].Id);
        Assert.Null(AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, Now));
        Assert.Equal(["Other"], new AgentTrust(vault).Remembered.Select(e => e.Client));
    }

    [Fact]
    public void TurningTheSettingOffForgetsEveryone()
    {
        var vault = new MemoryVault();
        var trust = new AgentTrust(vault);
        trust.Remember("Claude Code", Signed());
        trust.ForgetAll();
        Assert.Empty(trust.Remembered);
        Assert.Null(vault.Data);
        Assert.Empty(new AgentTrust(vault).Remembered);
    }

    [Fact]
    public void WhatCantBeReadLetsNobodyIn()
    {
        var shut = new MemoryVault(Encoding.UTF8.GetBytes($$"""[{"client":"Claude Code","requirement":"{{Claude}}"}]""")) { Usable = false };
        Assert.Empty(new AgentTrust(shut).Remembered);
        Assert.Empty(new AgentTrust(new MemoryVault("[{"u8.ToArray())).Remembered);
        Assert.False(new AgentTrust(shut).Remember("Claude Code", Signed()));
    }

    [Fact]
    public void ASimulatedEntryIsATestRunsOnly()
    {
        var trust = new AgentTrust(new MemoryVault());
        Assert.False(trust.RememberSimulated("Claude Code", "probe", testing: false));
        Assert.True(trust.RememberSimulated("Claude Code", "probe", testing: true));
        var python = Signed(@"C:\Python313\python.exe", "Python Software Foundation");
        Assert.Null(AgentTrust.Match("Claude Code", python, ["probe"], trust.Remembered, testing: false, Now));
        Assert.NotNull(AgentTrust.Match("Claude Code", python, ["probe"], trust.Remembered, testing: true, Now));
    }

    [Fact]
    public void StopSuspendsUntilAllowedAgain()
    {
        var vault = new MemoryVault();
        var trust = new AgentTrust(vault);
        trust.Remember("Claude Code", Signed());
        trust.Suspend("Claude Code");
        Assert.Null(AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, Now));
        Assert.Empty(trust.Requirements("Claude Code"));
        Assert.True(new AgentTrust(vault).Remembered[0].Suspended);
        trust.Resume("Codex");
        Assert.True(trust.Remembered[0].Suspended);
        trust.Resume("Claude Code");
        Assert.NotNull(AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, Now));
    }

    [Fact]
    public void StopSuspendsTheEntryThatLetItInUnderAnotherName()
    {
        var trust = new AgentTrust(new MemoryVault());
        trust.Remember("Claude Code", Signed());
        trust.Suspend("Renamed", also: trust.Remembered[0].Id);
        Assert.True(trust.Remembered[0].Suspended);
    }

    [Fact]
    public void ASuspensionTheDiskRefusesStillHolds()
    {
        var vault = new MemoryVault();
        var trust = new AgentTrust(vault);
        trust.Remember("Claude Code", Signed());
        vault.Refuses = true;
        trust.Suspend("Claude Code");
        Assert.Null(AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, Now));
    }

    [Fact]
    public void ItRunsOut()
    {
        var vault = new MemoryVault();
        var granted = Now.AddDays(-29);
        var trust = new AgentTrust(vault, granted);
        trust.Remember("Claude Code", Signed(), granted);
        bool Admitted(DateTimeOffset at) => AgentTrust.Match("Claude Code", Signed(), [Claude], trust.Remembered, testing: false, at) is not null;
        Assert.True(Admitted(granted.AddDays(6)));
        Assert.False(Admitted(granted.AddDays(7)), "7 days unused");
        // Used every few days: still 30 days at most from when it was granted.
        for (var days = 5; days <= 25; days += 5) trust.Used(trust.Remembered[0], granted.AddDays(days));
        Assert.True(Admitted(granted.AddDays(29)));
        Assert.False(Admitted(granted.AddDays(30)), "30 days after it was granted");
        Assert.Equal(granted.AddDays(30), trust.Remembered[0].Expires);
        // Read after it ran out: forgotten for good.
        Assert.Empty(new AgentTrust(vault, granted.AddDays(31)).Remembered);
        Assert.Null(vault.Data);
    }

    [Fact]
    public void YourTabsAreNeverRemembered()
    {
        var vault = new MemoryVault();
        new AgentTrust(vault).Remember("Claude Code", Signed());
        Assert.DoesNotContain("tabs", Encoding.UTF8.GetString(vault.Data!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASettingWithoutItsMarkIsTurnedOffAtLaunch()
    {
        TempStore.Ready();
        var prefs = new Prefs { Agents = true };
        prefs.CheckAgents(marked: true, testing: false);
        Assert.True(prefs.Agents);
        prefs.CheckAgents(marked: false, testing: true);
        Assert.True(prefs.Agents);
        prefs.CheckAgents(marked: false, testing: false);
        Assert.False(prefs.Agents);
        Assert.True(prefs.AgentsRefused);
        Assert.DoesNotContain("agentsRefused", System.Text.Json.JsonSerializer.Serialize(prefs));
    }
}
