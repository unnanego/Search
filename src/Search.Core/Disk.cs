namespace Search.Core;

// Files written in the background, one at a time and the newest last (Disk in Store.swift).
//
// Every write joins one serial queue, and a write that a newer one of the same file has overtaken is dropped,
// including when the newer one was written at once on the way out. Without that, the session written at quit
// could be overwritten by an older one still on its way: yesterday's tabs coming back instead of today's.
public static class Disk
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, long> _newest = new(StringComparer.OrdinalIgnoreCase);
    private static long _count;
    private static Task _tail = Task.CompletedTask;

    /// `encode` runs where the write does. `now` writes on the calling thread: quitting doesn't wait for a queue.
    public static Task Write(string file, Func<byte[]?> encode, bool now = false)
    {
        long turn;
        lock (_lock) { turn = ++_count; _newest[file] = turn; }
        if (now) { Put(file, turn, encode); return Task.CompletedTask; }
        lock (_lock) return _tail = _tail.ContinueWith(_ => Put(file, turn, encode), TaskScheduler.Default);
    }

    /// Waits for every write asked for so far.
    public static Task Flush() { lock (_lock) return _tail; }

    private static bool Current(string file, long turn) { lock (_lock) return _newest.TryGetValue(file, out var n) && n == turn; }

    private static void Put(string file, long turn, Func<byte[]?> encode)
    {
        if (!Current(file, turn) || encode() is not { } data) return;
        // Written beside it with no lock held, and only put in its place under the lock, so a write at once on
        // the way out never waits for a big one in the background.
        var folder = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(folder);
        var aside = Path.Combine(folder, $".{Path.GetFileName(file)}.{turn}.writing");
        try
        {
            File.WriteAllBytes(aside, data);
            lock (_lock) { if (Current(file, turn)) File.Move(aside, file, overwrite: true); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { if (File.Exists(aside)) try { File.Delete(aside); } catch (IOException) { } }
    }
}
