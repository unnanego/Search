using System.Security.Cryptography;
using System.Text;
using Search.Core;

namespace Search.Engine;

// The mark a switch in Settings leaves beside the setting itself (SwitchMark in PageHands.swift), and the file the
// agents you said to always allow are kept in. The Mac keeps both in its keychain, under Search's own access group;
// here each is a file in the store folder sealed with DPAPI for the person signed in to Windows, as the passwords are
// (Vault.cs). settings.json is plain text anything can write; a setting found on at launch without its mark is put
// back to off (Prefs.CheckAgents).
//
// What this keeps out: a settings file written by hand, copied from another account or PC, or left by an installer.
// What it can't: DPAPI opens for any program running as you, so one that sets out to forge the mark can. On the Mac
// only an app signed as Search can; Windows has no drawer of that kind for an unpackaged app.

/// A switch's mark: there or not, for this world's folder only.
public sealed class SwitchMark(string name)
{
    public static readonly SwitchMark Agents = new("agents");
    private static readonly byte[] _entropy = "Search browser switch mark v1"u8.ToArray();

    private string File => Store.File(name + ".mark");
    /// What the mark says: which switch, and which world's folder, so a mark copied into another world's isn't one.
    private byte[] Said => Encoding.UTF8.GetBytes($"{name} switched on in {Store.Folder}");

    public bool Given
    {
        get
        {
            try { return System.IO.File.Exists(File) && ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), _entropy, DataProtectionScope.CurrentUser).AsSpan().SequenceEqual(Said); }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public bool Grant() => SealedVault.Put(File, ProtectedData.Protect(Said, _entropy, DataProtectionScope.CurrentUser));

    public void Revoke() { try { System.IO.File.Delete(File); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}

/// A file in the store folder sealed with DPAPI for the person signed in: where the agents you always allow are kept.
public sealed class SealedVault(string file, string purpose) : IAgentVault
{
    /// Agents you said to always allow (AgentTrust).
    public static SealedVault Agents => new("agents.trusted", "Search browser trusted agents v1");
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);
    private string Path => Store.File(file);

    public bool Usable => true;

    public byte[]? Read()
    {
        try { return System.IO.File.Exists(Path) ? ProtectedData.Unprotect(System.IO.File.ReadAllBytes(Path), _entropy, DataProtectionScope.CurrentUser) : null; }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Write(byte[] data) => Put(Path, ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser));

    public void Erase() { try { System.IO.File.Delete(Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    /// Written beside it and moved into place, so a half-written file is never read.
    internal static bool Put(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var aside = path + ".writing";
            System.IO.File.WriteAllBytes(aside, data);
            System.IO.File.Move(aside, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// The switch in Settings and what hangs on it, for the window to call.
public static class AgentSwitch
{
    /// At launch, before anything listens: on without its mark is put back to off.
    public static void Check(Prefs prefs) => prefs.CheckAgents(SwitchMark.Agents.Given);

    /// Turned on or off in Settings: the mark left or taken, and everyone remembered forgotten (turned on afresh too).
    public static void Turned(bool on, AgentTrust trust)
    {
        if (on) SwitchMark.Agents.Grant(); else SwitchMark.Agents.Revoke();
        trust.ForgetAll();
    }
}
