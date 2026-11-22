using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Search.Core;

// How every import reads another browser's files (ImportRead and Snapshot in Import.swift, the scratch folder in
// ImportFile.swift). A profile's files can be written by anything running as you, so each is read as a damaged one
// could be: a regular file only, never a link or another reparse point followed at the end, and only where the
// handle actually opened lies inside the browser's own folder (the final path Windows gives for it, so a junction
// part-way down can't lead out). Sizes are capped before the read and during it, in case the file grows.
//
// A database is never opened where it is: the browser is usually running, holds it locked, and keeps its newest
// writes in "-wal" beside it. It is copied, with that, into a folder of Search's own named for this process, and the
// copy is read; the folder goes when the copy does. One left behind by a Search that stopped mid-import is swept at
// the next launch (Sweep), and only a dead process's.
public static class ImportRead
{
    /// JSON and session files: no real one is anywhere near this.
    public const long JsonLimit = 64L << 20;
    /// A database copy. History grows past the JSON limit in a few years of use, so its own, larger cap.
    public const long DatabaseLimit = 1L << 30;

    /// Where scratch folders are made; the system's temp folder unless a test says otherwise.
    public static string TempRoot { get; set; } = Path.GetTempPath();

    private const string ScratchPrefix = "search-import-";

    /// The file's bytes, read through a handle checked as above. Throws IOException for anything refused.
    public static byte[] Data(string file, string inside, long limit = JsonLimit)
    {
        using var handle = OpenRegular(file, inside, limit);
        using var stream = new FileStream(handle, FileAccess.Read, 1);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit) throw new IOException("Larger than any real one.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// Null instead of throwing, for a file that is optional or allowed to be missing.
    public static byte[]? TryData(string file, string inside, long limit = JsonLimit)
    {
        try { return Data(file, inside, limit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// A handle to a regular file, not a link, its final path inside `inside` (when given), no larger than `limit`.
    /// Shared for reading, writing and deleting, so the browser that owns it never notices.
    public static SafeFileHandle OpenRegular(string file, string? inside = null, long? limit = null)
    {
        var handle = CreateFileW(LongPath(file), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw error is 2 or 3 ? new FileNotFoundException("Not there.", file) : new IOException(new Win32Exception(error).Message);
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Unreadable.");
            if ((info.Attributes & (ReparsePointAttribute | DirectoryAttribute | DeviceAttribute)) != 0) throw new IOException("Not a regular file.");
            var size = ((long)info.SizeHigh << 32) | info.SizeLow;
            if (limit is { } most && size > most) throw new IOException("Larger than any real one.");
            if (inside is not null && (FinalPath(inside) is not { } root || FinalPath(handle) is not { } opened || !Within(opened, root))) throw new IOException("Outside the browser's folder.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// Whether `path` is `root` or somewhere inside it, both final paths.
    internal static bool Within(string path, string root)
    {
        var trimmed = root.TrimEnd('\\');
        return path.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || path.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// Where a folder or file really is, links and junctions on the way resolved; null when it can't be opened.
    public static string? FinalPath(string path)
    {
        using var handle = CreateFileW(LongPath(path), 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    /// Whether `path` resolves to somewhere inside `root` (both resolved): the containment check without a read.
    public static bool Inside(string path, string root) => FinalPath(path) is { } at && FinalPath(root) is { } top && Within(at, top);

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
        if (length > buffer.Capacity) { buffer = new StringBuilder((int)length + 1); length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0); }
        if (length == 0 || length > buffer.Capacity) return null;
        var text = buffer.ToString();
        return text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + text[8..] : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    /// Whether it is a plain folder: there, a directory, and not a link or junction.
    public static bool IsPlainFolder(string path)
    {
        try { var info = new DirectoryInfo(path); return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // The scratch folder.

    /// A folder of its own for what an import copies, named for this process so another Search beside it never takes
    /// it for a leftover.
    public static string ScratchFolder() => Path.Combine(TempRoot, $"{ScratchPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");

    /// At launch: what an import left behind when Search stopped in the middle of it, folders of a process that is
    /// gone, or ones not named for a process that are a day old. Never a link, never anything else in the folder.
    public static void Sweep(string? root = null)
    {
        string[] names;
        try { names = Directory.GetDirectories(root ?? TempRoot, ScratchPrefix + "*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var folder in names)
        {
            if (!IsPlainFolder(folder)) continue;
            var parts = Path.GetFileName(folder)[ScratchPrefix.Length..].Split('-', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pid))
            {
                if (pid == Environment.ProcessId || Running(pid)) continue;
            }
            else if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < TimeSpan.FromDays(1)) continue;
            try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // Not ours to look at still means it is there.
        catch (Win32Exception) { return true; }
    }

    // Win32: CreateFile without following the last link, and the path a handle really has.

    private const uint GenericRead = 0x80000000, ShareAll = 0x7, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint ReparsePointAttribute = 0x400, DirectoryAttribute = 0x10, DeviceAttribute = 0x40;

    /// BY_HANDLE_FILE_INFORMATION: its FILETIMEs are two DWORDs, so packed to four.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}

/// A copy of one of another browser's SQLite files to read from, with the "-wal" beside it when there is one: what
/// was written last is often still there, and a copy without it misses the newest history and passwords. The folder
/// it is in goes with it.
internal sealed class Snapshot : IDisposable
{
    public string File { get; }
    private readonly string _folder;

    /// Throws IOException when the file can't be copied (refused, missing, too large).
    public Snapshot(string source, string inside, long limit = ImportRead.DatabaseLimit)
    {
        _folder = ImportRead.ScratchFolder();
        Directory.CreateDirectory(_folder);
        File = Path.Combine(_folder, Path.GetFileName(source));
        try
        {
            Copy(source, File, inside, limit, optional: false);
            Copy(source + "-wal", File + "-wal", inside, limit, optional: true);
        }
        catch { Dispose(); throw; }
    }

    /// The copy, or null when there is none to read.
    public static Snapshot? Of(string source, string inside)
    {
        try { return new Snapshot(source, inside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// Opened read-only; null when it isn't SQLite.
    public Sqlite? Open() => Sqlite.Open(File);

    private static void Copy(string source, string target, string inside, long limit, bool optional)
    {
        SafeFileHandle input;
        try { input = ImportRead.OpenRegular(source, inside, limit); }
        catch (FileNotFoundException) when (optional) { return; }
        using (input)
        using (var from = new FileStream(input, FileAccess.Read, 1))
        using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
            var chunk = new byte[256 * 1024];
            long copied = 0;
            int read;
            while ((read = from.Read(chunk, 0, chunk.Length)) > 0)
            {
                if ((copied += read) > limit) throw new IOException("Larger than any real one.");
                to.Write(chunk, 0, read);
            }
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// Addresses as every import takes them: a web page's (http or https), with any name and password in it left behind.
public static class ImportUrl
{
    public static Uri? Page(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url)) return null;
        if (url.Scheme is not ("http" or "https") || url.Host.Length == 0) return null;
        if (url.UserInfo.Length == 0) return url;
        return new UriBuilder(url) { UserName = "", Password = "" }.Uri;
    }

    /// A name fit to keep: trimmed, at most 300 characters, null for nothing (Arc.named).
    public static string? Named(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed.Length > 300 ? trimmed[..300] : trimmed : null;

    /// A site as an icon is kept for it: the host, lowercase, without www.
    public static string? Site(Uri url) => url.Host is { Length: > 0 } host ? host.ToLowerInvariant() is var lower && lower.StartsWith("www.", StringComparison.Ordinal) ? lower[4..] : lower : null;
}
