using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Search.Core;

namespace Search.Engine;

// Who is at the other end of the agent's pipe, and which program started it (AgentIdentity in AgentTrust.swift).
//
// The client is found by the pid the pipe names (GetNamedPipeClientProcessId) and opened at once: a process held open
// keeps its pid, so it can't be handed to someone else while Search looks. It must run as you. Its parent is read from
// the client's own handle, and counts only if it was started before the client was (a pid freed and given to a newer
// process can't be anyone's parent) and is still running. The parent's executable is then checked for an Authenticode
// signature (WinVerifyTrust, offline: no revocation lists fetched, nothing sent anywhere) and the signer read from the
// very signature checked. Afterwards the chain is read again: the client still the one that connected, its parent the
// same pid, started at the same moment. Slow (the signature is checked on disk): never on the window's thread.
//
// Out of reach of this: a program running as you can rename a running executable and put another in its place, and
// can unseal what DPAPI sealed for you. The agent's limits (AgentServer) hold whoever is let in.

/// The process that connected, held open for as long as its session lasts.
public sealed class AgentPeer(int pid, SafeProcessHandle process, long created) : IDisposable
{
    public int Pid { get; } = pid;
    public SafeProcessHandle Process { get; } = process;
    /// When it started, as Windows counts (FILETIME).
    public long Created { get; } = created;
    public void Dispose() => Process.Dispose();
}

public static class AgentIdentity
{
    private const uint QueryLimited = 0x1000, TokenQuery = 0x0008, StillActive = 259;

    /// The client at the other end of a connected pipe, if it runs as you: opened, and held.
    public static AgentPeer? Peer(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out var pid) || pid == 0) return null;
        var process = OpenProcess(QueryLimited, false, pid);
        if (process.IsInvalid || Created(process) is not { } created || !Mine(process)) { process.Dispose(); return null; }
        return new AgentPeer((int)pid, process, created);
    }

    /// Whether a process runs as the Windows user this one does.
    public static bool Mine(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token)
        {
            try { using var identity = new WindowsIdentity(token.DangerousGetHandle()); using var me = WindowsIdentity.GetCurrent(); return identity.User is { } user && user == me.User; }
            catch (Exception e) when (e is ArgumentException or SecurityException or UnauthorizedAccessException) { return false; }
        }
    }

    /// "python.exe, started by claude.exe": what the question says beside the name the client gives itself.
    public static string Program(AgentPeer peer)
    {
        var name = Path.GetFileName(PathOf(peer.Process) ?? "");
        if (Parent(peer.Process) is not { } parent || Open(parent) is not { } process) return name;
        using (process)
        {
            var parentName = Path.GetFileName(PathOf(process) ?? "");
            return parentName.Length == 0 || parentName.Equals(name, StringComparison.OrdinalIgnoreCase) || Created(process) >= peer.Created ? name : $"{name}, started by {parentName}";
        }
    }

    /// The program that started the client, and which of `requirements` it passes; null when the chain can't be read,
    /// or changed while it was being read.
    public static (AgentLauncher Launcher, string[] Passed)? Examine(AgentPeer peer, IReadOnlyList<string> requirements)
    {
        if (!Alive(peer.Process) || Created(peer.Process) != peer.Created || Parent(peer.Process) is not { } parentPid || parentPid <= 4) return null;
        long born;
        string path;
        using (var parent = Open(parentPid))
        {
            if (parent is null || Created(parent) is not { } started || started >= peer.Created || !Alive(parent)) return null;
            born = started;
            path = PathOf(parent) ?? "";
        }
        var (signed, signer) = path.Length > 0 ? Signature(path) : (false, "");
        var launcher = new AgentLauncher { Path = path, Signed = signed, Signer = signer };
        var passed = requirements.Where(launcher.Passes).ToArray();
        // The same chain afterwards: the client still the one that connected, its parent the same pid, started at the same moment.
        if (!Alive(peer.Process) || Parent(peer.Process) != parentPid) return null;
        using var again = Open(parentPid);
        return again is not null && Created(again) == born && Alive(again) ? (launcher, passed) : null;
    }

    private static SafeProcessHandle? Open(int pid)
    {
        var process = OpenProcess(QueryLimited, false, (uint)pid);
        if (!process.IsInvalid) return process;
        process.Dispose();
        return null;
    }

    public static bool Alive(SafeProcessHandle process) => GetExitCodeProcess(process, out var code) && code == StillActive;

    public static long? Created(SafeProcessHandle process) => GetProcessTimes(process, out var created, out _, out _, out _) ? created : null;

    public static int? Parent(SafeProcessHandle process)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : null;
    }

    public static string? PathOf(SafeProcessHandle process)
    {
        var buffer = new char[32768];
        var size = buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    /// Whether a file carries an Authenticode signature this PC trusts for code, and who signed it, read from the very
    /// signature checked. The file is held open against writes while it is.
    public static (bool Signed, string Signer) Signature(string path)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return (false, ""); }
        using (file)
        {
            var fileInfo = new WintrustFileInfo { Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), Path = path, File = file.SafeFileHandle.DangerousGetHandle() };
            var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePointer, false);
                var data = new WintrustData
                {
                    Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = 2 /* none */, RevocationChecks = 0 /* none */, UnionChoice = 1 /* file */, File = filePointer,
                    StateAction = 1 /* verify */, ProvFlags = 0x10 /* no revocation check */ | 0x1000 /* cache only: nothing fetched */
                };
                var action = GenericVerifyV2;
                var verdict = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                var signer = "";
                try
                {
                    if (verdict == 0 && WTHelperProvDataFromStateData(data.StateData) is var provider && provider != IntPtr.Zero
                        && WTHelperGetProvSignerFromChain(provider, 0, false, 0) is var sgnr && sgnr != IntPtr.Zero
                        && WTHelperGetProvCertFromChain(sgnr, 0) is var cert && cert != IntPtr.Zero
                        && Marshal.ReadIntPtr(cert, IntPtr.Size) is var context && context != IntPtr.Zero)
                    {
                        // CRYPT_PROVIDER_CERT: a DWORD, then the certificate's context.
                        using var leaf = new X509Certificate2(context);
                        signer = leaf.GetNameInfo(X509NameType.SimpleName, false);
                    }
                }
                finally
                {
                    data.StateAction = 2; // close
                    WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                }
                return (verdict == 0, signer);
            }
            finally { Marshal.DestroyStructure<WintrustFileInfo>(filePointer); Marshal.FreeHGlobal(filePointer); }
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProvFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
    [DllImport("wintrust.dll")] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll")] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint cert);
}
