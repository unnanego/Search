using System.Text.Json;
using System.Text.Json.Serialization;

namespace Search.Core;

// "Always allow": an agent remembered, so its question isn't asked again (AgentTrust.swift). The rules, here; how the
// program is found and its signature read is the engine's (AgentIdentity.cs), and where it is kept too (a DPAPI file).
//
// What is remembered is never the name a client gives itself, which is its own word. It is the program that started
// the client (Claude Code's claude.exe, which runs search-mcp under Python), as its Authenticode signature says: a
// requirement Search writes for it (its file name and the signer that must have signed it), with the client's name
// beside it. Python itself is never enough: anything can run it.
//
// Offered only for a parent on a short list of agents' own programs, each signed by its maker, never a terminal, an
// editor, an interpreter or a shell, which run whatever they are given. Checked again on every connection: the parent
// must pass the requirement remembered, and the client give the same name; anything else and the question is asked.
// Stop suspends it (asked again until you tick "Always allow" again), and it runs out by itself 30 days after it was
// granted or 7 days after it was last used. It never carries "Let it see my open tabs". Turning the setting off, or
// on, forgets everyone.

/// The program that started an agent's client, as its signature says.
public sealed record AgentLauncher
{
    /// Its executable, as it ran.
    public string Path { get; init; } = "";
    /// WinVerifyTrust said yes: an intact Authenticode signature, from a certificate this PC trusts for code.
    public bool Signed { get; init; }
    /// Who signed it, the leaf certificate's name: "Anthropic, PBC".
    public string Signer { get; init; } = "";

    public string Name => global::System.IO.Path.GetFileName(Path);

    /// The only programs an agent can be remembered as started by: their file name, and who must have signed it. Not a
    /// list of what to keep out (terminals, editors, interpreters and shells all run anything they are given) but of
    /// what to let in: agents' own command-line programs, each signed by its maker.
    public static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude.exe"] = "Anthropic, PBC"
    };

    /// What a launcher is checked against and remembered by: written here, never the program's own word.
    public static string Requirement(string name, string signer) => $"{name.ToLowerInvariant()} signed by {signer}";

    /// Why it can't be remembered, or null when it can.
    public string? Unrememberable => Refusal(Signed, Signer, Name);

    /// The requirement it is remembered by: the allowlist's own, once it has passed it.
    public string? Allowlisted => Unrememberable is null ? Requirement(Name, Allowed[Name]) : null;

    /// Whether it passes a remembered requirement: signed, and by the name and signer the requirement says.
    public bool Passes(string requirement) => Signed && Signer.Length > 0 && requirement.Length > 0 && requirement == Requirement(Name, Signer);

    public static string? Refusal(bool signed, string signer, string name)
    {
        var called = name.Length == 0 ? "the program that started it" : name;
        if (!signed) return $"{called}, which started it, isn't signed";
        if (signer.Length == 0) return $"{called}, which started it, isn't signed by a named developer";
        return Allowed.TryGetValue(name, out var expected) && expected == signer ? null : $"it was started by {called}, which isn't an agent's own program Search knows";
    }
}

/// One agent you said to always allow.
public sealed record RememberedAgent
{
    /// The name it gave itself, as shown ("Claude Code").
    public required string Client { get; init; }
    /// The requirement the program that started it passed.
    public required string Requirement { get; init; }
    /// "Anthropic, PBC".
    public string Signer { get; init; } = "";
    /// Where that program was, the last time: shown, not checked (an update moves it).
    public string Path { get; init; } = "";
    public DateTimeOffset Added { get; init; }
    public DateTimeOffset Used { get; init; }
    /// Stopped: asked again, until you tick "Always allow" for it again in the question.
    public bool? Suspended { get; init; }
    /// Put there by the bench in a test world, for a program that couldn't otherwise be remembered. Never honoured
    /// outside a test run.
    public bool? Simulated { get; init; }

    [JsonIgnore] public string Id => Client + "\n" + Requirement;

    /// Remembered for this long after it was granted, and for this long after it was last let in, whichever ends first.
    public static readonly TimeSpan Lasts = TimeSpan.FromDays(30), IdleLasts = TimeSpan.FromDays(7);

    [JsonIgnore] public DateTimeOffset Expires => Added + Lasts < Used + IdleLasts ? Added + Lasts : Used + IdleLasts;
    public bool Expired(DateTimeOffset now) => now >= Expires;
}

/// Where the remembered agents are kept.
public interface IAgentVault
{
    /// There is somewhere safe to keep them.
    bool Usable { get; }
    byte[]? Read();
    bool Write(byte[] data);
    void Erase();
}

public sealed class AgentTrust
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly IAgentVault _vault;

    public IReadOnlyList<RememberedAgent> Remembered { get; private set; } = [];
    /// Whether this copy can keep anyone, asked once.
    public bool Usable { get; }
    /// Someone remembered, suspended, used or forgotten: for Settings' list.
    public event Action? Changed;

    public AgentTrust(IAgentVault vault, DateTimeOffset? now = null)
    {
        _vault = vault;
        Usable = vault.Usable;
        if (!Usable || vault.Read() is not { } data) return;
        List<RememberedAgent>? found;
        try { found = JsonSerializer.Deserialize<List<RememberedAgent>>(data, _json); } catch (JsonException) { found = null; }
        if (found is null) return;
        Remembered = found;
        // What has run out is forgotten for good.
        var at = now ?? DateTimeOffset.UtcNow;
        if (found.Any(e => e.Expired(at))) Save(found.Where(e => !e.Expired(at)).ToList());
    }

    /// Remembered as allowed from now on: the client by its name, the program that started it by its signature. Only
    /// a launcher that can be.
    public bool Remember(string client, AgentLauncher launcher, DateTimeOffset? now = null)
    {
        if (!Usable || launcher.Allowlisted is not { } requirement) return false;
        var at = now ?? DateTimeOffset.UtcNow;
        return Keep(new RememberedAgent { Client = client, Requirement = requirement, Signer = launcher.Signer, Path = launcher.Path, Added = at, Used = at });
    }

    /// Stopped: asked again from now on, until "Always allow" is ticked for it again. Every entry under the name it
    /// gave, and the one that let it in if another. Fails closed: if the file won't take it, it holds until Search quits.
    public void Suspend(string client, string? also = null)
    {
        var next = Remembered.Select(e => e.Client == client || e.Id == also ? e with { Suspended = true } : e).ToList();
        if (next.SequenceEqual(Remembered)) return;
        if (!Save(next)) { Remembered = next; Changed?.Invoke(); }
    }

    /// "Always allow" ticked again in the question: what was suspended under that name is let in again next time.
    public void Resume(string client)
    {
        if (!Remembered.Any(e => e.Client == client && e.Suspended == true)) return;
        Save(Remembered.Select(e => e.Client == client ? e with { Suspended = null } : e).ToList());
    }

    /// The bench's, in a test world only: a requirement remembered for a launcher that couldn't otherwise be.
    public bool RememberSimulated(string client, string requirement, bool testing, DateTimeOffset? added = null)
    {
        if (!testing || !Usable || requirement.Length == 0) return false;
        var at = added ?? DateTimeOffset.UtcNow;
        return Keep(new RememberedAgent { Client = client, Requirement = requirement, Signer = "a probe", Added = at, Used = at, Simulated = true });
    }

    private bool Keep(RememberedAgent entry) => Save(Remembered.Where(e => e.Id != entry.Id).Append(entry).ToList());

    /// Asked again from now on.
    public void Forget(string id) => Save(Remembered.Where(e => e.Id != id).ToList());

    public void ForgetAll() { _vault.Erase(); Remembered = []; Changed?.Invoke(); }

    /// Last let in now.
    public void Used(RememberedAgent entry, DateTimeOffset? now = null)
    {
        if (!Remembered.Any(e => e.Id == entry.Id)) return;
        Save(Remembered.Select(e => e.Id == entry.Id ? e with { Used = now ?? DateTimeOffset.UtcNow, Path = entry.Path } : e).ToList());
    }

    private bool Save(List<RememberedAgent> next)
    {
        if (!Usable) return false;
        if (next.Count == 0) _vault.Erase();
        else if (!_vault.Write(JsonSerializer.SerializeToUtf8Bytes(next, _json))) return false;
        Remembered = next;
        Changed?.Invoke();
        return true;
    }

    /// The requirements to check a client by, before it is let in: those remembered under the name it gave.
    public IReadOnlyList<string> Requirements(string client, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return Remembered.Where(e => e.Client == client && e.Suspended != true && !e.Expired(at)).Select(e => e.Requirement).ToArray();
    }

    /// The entry that lets a connection in without asking, if one does: the same name, and the program that started
    /// it passing that entry's requirement (`passed`) and still one that could be remembered. Nothing read, nobody in.
    public static RememberedAgent? Match(string client, AgentLauncher? launcher, IReadOnlyCollection<string> passed, IEnumerable<RememberedAgent> remembered, bool testing, DateTimeOffset now)
    {
        if (launcher is null) return null;
        return remembered.FirstOrDefault(e => e.Client == client && passed.Contains(e.Requirement) && e.Suspended != true && !e.Expired(now) && (e.Simulated == true ? testing : launcher.Unrememberable is null));
    }
}
