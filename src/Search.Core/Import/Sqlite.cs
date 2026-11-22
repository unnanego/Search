using System.Runtime.InteropServices;
using System.Text;

namespace Search.Core;

// SQLite through the copy Windows itself ships (winsqlite3.dll, in System32 since Windows 10), for reading another
// browser's databases without bringing a package along. As small as the readers need: open, run a statement with a
// value or two bound, read its columns, close. The readers only ever open a copy (see Snapshot), read-only; tests
// open read-write to make their fixtures.
internal sealed class Sqlite : IDisposable
{
    private const string Lib = "winsqlite3.dll";
    private const int Ok = 0, RowReady = 100, Done = 101;
    private const int OpenReadOnly = 0x1, OpenReadWrite = 0x2, OpenCreate = 0x4;
    /// SQLITE_TRANSIENT: SQLite copies a bound value before the call returns.
    private static readonly IntPtr Transient = new(-1);

    public const int Integer = 1, Float = 2, Text = 3, Blob = 4, Null = 5;

    private IntPtr _db;

    private Sqlite(IntPtr db) => _db = db;

    /// Null for a file that isn't there, isn't SQLite, or a Windows without winsqlite3.
    public static Sqlite? Open(string file, bool readOnly = true)
    {
        try
        {
            var flags = readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate;
            if (sqlite3_open_v2(Utf8(file), out var db, flags, IntPtr.Zero) == Ok) return new Sqlite(db);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return null;
    }

    /// Statements with nothing to read back; throws with SQLite's own words when one fails (fixtures, not imports).
    public void Exec(string sql)
    {
        if (sqlite3_exec(_db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error) == Ok) return;
        var said = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(error) : null;
        if (error != IntPtr.Zero) sqlite3_free(error);
        throw new InvalidOperationException(said ?? "SQLite said no.");
    }

    /// Each row of a statement, valid until the next one is asked for. Values are bound in order: a string, a long,
    /// a byte[] or null. Nothing at all for a statement that doesn't prepare (a table or column this file lacks).
    public IEnumerable<SqliteRow> Rows(string sql, params object?[] values)
    {
        if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero) != Ok || statement == IntPtr.Zero) yield break;
        try
        {
            for (var i = 0; i < values.Length; i++) Bind(statement, i + 1, values[i]);
            while (sqlite3_step(statement) == RowReady) yield return new SqliteRow(statement);
        }
        finally { sqlite3_finalize(statement); }
    }

    /// Whether a statement prepares at all here: the table and columns are there.
    public bool Prepares(string sql)
    {
        if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero) != Ok || statement == IntPtr.Zero) return false;
        sqlite3_finalize(statement);
        return true;
    }

    /// The first column of the first row as a number: a COUNT(*). Null when the statement doesn't run.
    public long? Scalar(string sql, params object?[] values)
    {
        foreach (var row in Rows(sql, values)) return row.Long(0);
        return null;
    }

    /// For a statement that changes something (fixtures): its values bound, run to the end.
    public void Run(string sql, params object?[] values)
    {
        if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero) != Ok) throw new InvalidOperationException(Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
        try
        {
            for (var i = 0; i < values.Length; i++) Bind(statement, i + 1, values[i]);
            if (sqlite3_step(statement) is not (Done or RowReady)) throw new InvalidOperationException(Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
        }
        finally { sqlite3_finalize(statement); }
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    private static void Bind(IntPtr statement, int at, object? value)
    {
        switch (value)
        {
            case null: sqlite3_bind_null(statement, at); break;
            case string text: var bytes = Encoding.UTF8.GetBytes(text); sqlite3_bind_text(statement, at, bytes, bytes.Length, Transient); break;
            case byte[] blob: sqlite3_bind_blob(statement, at, blob, blob.Length, Transient); break;
            case double real: sqlite3_bind_double(statement, at, real); break;
            case bool flag: sqlite3_bind_int64(statement, at, flag ? 1 : 0); break;
            default: sqlite3_bind_int64(statement, at, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern void sqlite3_free(IntPtr memory);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_bind_null(IntPtr statement, int at);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_bind_int64(IntPtr statement, int at, long value);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_bind_double(IntPtr statement, int at, double value);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_bind_text(IntPtr statement, int at, byte[] value, int length, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_bind_blob(IntPtr statement, int at, byte[] value, int length, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_column_count(IntPtr statement);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_column_type(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern double sqlite3_column_double(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
}

/// One row of a statement, read column by column; good only while the statement stands on it.
internal readonly struct SqliteRow(IntPtr statement)
{
    public int Count => Sqlite.sqlite3_column_count(statement);
    public int Type(int column) => Sqlite.sqlite3_column_type(statement, column);
    public string? Name(int column) => Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_name(statement, column));
    public bool IsNull(int column) => Type(column) == Sqlite.Null;
    public long Long(int column) => Sqlite.sqlite3_column_int64(statement, column);
    public double Double(int column) => Sqlite.sqlite3_column_double(statement, column);

    /// Null for a NULL; the bytes as UTF-8 otherwise, whatever the column was declared.
    public string? Text(int column)
    {
        if (IsNull(column)) return null;
        var at = Sqlite.sqlite3_column_text(statement, column);
        var length = Sqlite.sqlite3_column_bytes(statement, column);
        return at == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(at, length);
    }

    public byte[] Blob(int column)
    {
        var at = Sqlite.sqlite3_column_blob(statement, column);
        var length = Sqlite.sqlite3_column_bytes(statement, column);
        if (at == IntPtr.Zero || length <= 0) return [];
        var bytes = new byte[length];
        Marshal.Copy(at, bytes, 0, length);
        return bytes;
    }
}
