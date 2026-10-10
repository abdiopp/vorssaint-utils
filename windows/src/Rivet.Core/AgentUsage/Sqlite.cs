// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Agents;

/// <summary>
/// A minimal read-mostly SQLite binding for the OpenCode database, loaded at
/// run time so no package is needed: Windows ships <c>winsqlite3.dll</c>
/// (System32, Windows 10+), macOS has <c>libsqlite3.dylib</c> and Linux
/// <c>libsqlite3.so.0</c>. A bundled <c>e_sqlite3</c>/<c>sqlite3</c> is used
/// when present. Without any library OpenCode is reported as unavailable.
/// <para>
/// On Windows the search never uses PATH: other programs put their own
/// <c>sqlite3.dll</c> there (the AWS CLI does), and loading a stranger's build
/// crashed the test host and would let any such DLL run inside the app. Bundled
/// copies come only from the app's folder, the system copy only from System32.
/// </para>
/// </summary>
public static class SqliteNative
{
    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;
    public const int OpenReadOnly = 0x00000001;
    public const int OpenReadWrite = 0x00000002;
    public const int OpenCreate = 0x00000004;
    public const int OpenUri = 0x00000040;
    public const int OpenNoMutex = 0x00008000;
    public const int TypeNull = 5;

    private static readonly Lazy<Api?> Loaded = new(Load);

    public static bool IsAvailable => Loaded.Value is not null;

    internal static Api Functions => Loaded.Value ?? throw new InvalidOperationException("SQLite is not available.");

    private static Api? Load()
    {
        foreach (var (name, handle) in Candidates())
        {
            try
            {
                return new Api(handle);
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Warn("agents", $"{name} lacks SQLite exports.", ex);
            }
        }

        Log.Warn("agents", "No SQLite library found; OpenCode usage cannot be read.");
        return null;
    }

    private static IEnumerable<(string Name, nint Handle)> Candidates()
    {
        var assembly = typeof(SqliteNative).Assembly;
        if (OperatingSystem.IsWindows())
        {
            // Absolute paths only: a bare name lets Windows fall back to its default search, PATH included.
            string[] paths =
            [
                Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll"),
                Path.Combine(AppContext.BaseDirectory, "sqlite3.dll"),
                Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"),
            ];
            foreach (var path in paths)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    yield return (path, handle);
                }
            }

            yield break;
        }

        string[] names = OperatingSystem.IsMacOS()
            ? ["libe_sqlite3", "/usr/lib/libsqlite3.dylib", "libsqlite3.dylib", "libsqlite3"]
            : ["libe_sqlite3", "libsqlite3.so.0", "libsqlite3.so", "libsqlite3"];
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, assembly, null, out var handle) || NativeLibrary.TryLoad(name, out handle))
            {
                yield return (name, handle);
            }
        }
    }

    internal sealed class Api
    {
        public Api(nint lib)
        {
            OpenV2 = Get<OpenV2Fn>(lib, "sqlite3_open_v2");
            CloseV2 = Get<CloseFn>(lib, "sqlite3_close_v2");
            BusyTimeout = Get<BusyTimeoutFn>(lib, "sqlite3_busy_timeout");
            PrepareV2 = Get<PrepareFn>(lib, "sqlite3_prepare_v2");
            Step = Get<StmtFn>(lib, "sqlite3_step");
            Reset = Get<StmtFn>(lib, "sqlite3_reset");
            Finalize = Get<StmtFn>(lib, "sqlite3_finalize");
            BindInt64 = Get<BindInt64Fn>(lib, "sqlite3_bind_int64");
            BindText = Get<BindTextFn>(lib, "sqlite3_bind_text");
            ColumnType = Get<ColumnIntFn>(lib, "sqlite3_column_type");
            ColumnBytes = Get<ColumnIntFn>(lib, "sqlite3_column_bytes");
            ColumnInt64 = Get<ColumnInt64Fn>(lib, "sqlite3_column_int64");
            ColumnDouble = Get<ColumnDoubleFn>(lib, "sqlite3_column_double");
            ColumnText = Get<ColumnPtrFn>(lib, "sqlite3_column_text");
            ErrMsg = Get<ErrMsgFn>(lib, "sqlite3_errmsg");
        }

        public OpenV2Fn OpenV2 { get; }

        public CloseFn CloseV2 { get; }

        public BusyTimeoutFn BusyTimeout { get; }

        public PrepareFn PrepareV2 { get; }

        public StmtFn Step { get; }

        public StmtFn Reset { get; }

        public StmtFn Finalize { get; }

        public BindInt64Fn BindInt64 { get; }

        public BindTextFn BindText { get; }

        public ColumnIntFn ColumnType { get; }

        public ColumnIntFn ColumnBytes { get; }

        public ColumnInt64Fn ColumnInt64 { get; }

        public ColumnDoubleFn ColumnDouble { get; }

        public ColumnPtrFn ColumnText { get; }

        public ErrMsgFn ErrMsg { get; }

        private static T Get<T>(nint lib, string name)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OpenV2Fn(byte[] filename, out nint db, int flags, nint vfs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CloseFn(nint db);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int BusyTimeoutFn(nint db, int ms);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PrepareFn(nint db, byte[] sql, int bytes, out nint statement, nint tail);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int StmtFn(nint statement);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int BindInt64Fn(nint statement, int index, long value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int BindTextFn(nint statement, int index, byte[] text, int bytes, nint destructor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ColumnIntFn(nint statement, int column);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate long ColumnInt64Fn(nint statement, int column);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate double ColumnDoubleFn(nint statement, int column);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint ColumnPtrFn(nint statement, int column);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint ErrMsgFn(nint db);
}

public sealed class SqliteException(string message) : Exception(message);

/// <summary>An open database connection.</summary>
public sealed class SqliteDatabase : IDisposable
{
    private nint _db;

    private SqliteDatabase(nint db)
    {
        _db = db;
    }

    /// <summary>Opens a database read-only (WAL databases need their -shm/-wal files accessible).</summary>
    public static SqliteDatabase OpenReadOnly(string path, int busyTimeoutMs = 2000) =>
        Open(path, SqliteNative.OpenReadOnly | SqliteNative.OpenNoMutex, busyTimeoutMs);

    /// <summary>Opens or creates a database for writing (tests build fixtures with it).</summary>
    public static SqliteDatabase OpenReadWrite(string path) =>
        Open(path, SqliteNative.OpenReadWrite | SqliteNative.OpenCreate | SqliteNative.OpenNoMutex, 2000);

    public SqliteStatement Prepare(string sql)
    {
        var api = SqliteNative.Functions;
        var bytes = Encoding.UTF8.GetBytes(sql + "\0");
        var code = api.PrepareV2(_db, bytes, bytes.Length, out var statement, 0);
        if (code != SqliteNative.Ok)
        {
            throw new SqliteException($"prepare failed ({code}): {Error()}");
        }

        return new SqliteStatement(statement);
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    public void Execute(string sql)
    {
        using var statement = Prepare(sql);
        while (statement.Step())
        {
        }
    }

    /// <summary>Whether a scalar query returns a row (feature checks such as JSON1).</summary>
    public bool TryScalar(string sql, out string? value)
    {
        value = null;
        try
        {
            using var statement = Prepare(sql);
            if (statement.Step())
            {
                value = statement.Text(0);
            }

            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            SqliteNative.Functions.CloseV2(_db);
            _db = 0;
        }
    }

    private static SqliteDatabase Open(string path, int flags, int busyTimeoutMs)
    {
        var api = SqliteNative.Functions;
        var code = api.OpenV2(Encoding.UTF8.GetBytes(path + "\0"), out var db, flags, 0);
        if (code != SqliteNative.Ok)
        {
            if (db != 0)
            {
                api.CloseV2(db);
            }

            throw new SqliteException($"open failed ({code})");
        }

        api.BusyTimeout(db, busyTimeoutMs);
        return new SqliteDatabase(db);
    }

    private string Error()
    {
        var message = SqliteNative.Functions.ErrMsg(_db);
        return message == 0 ? string.Empty : Marshal.PtrToStringUTF8(message) ?? string.Empty;
    }
}

/// <summary>A prepared statement; columns are 0-based, parameters 1-based.</summary>
public sealed class SqliteStatement : IDisposable
{
    private static readonly nint Transient = -1;
    private nint _statement;

    internal SqliteStatement(nint statement)
    {
        _statement = statement;
    }

    public SqliteStatement Bind(int index, long value)
    {
        SqliteNative.Functions.BindInt64(_statement, index, value);
        return this;
    }

    public SqliteStatement Bind(int index, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        SqliteNative.Functions.BindText(_statement, index, bytes, bytes.Length, Transient);
        return this;
    }

    /// <summary>Advances to the next row; false when done.</summary>
    public bool Step()
    {
        var code = SqliteNative.Functions.Step(_statement);
        return code switch
        {
            SqliteNative.Row => true,
            SqliteNative.Done => false,
            _ => throw new SqliteException($"step failed ({code})"),
        };
    }

    public void Reset() => SqliteNative.Functions.Reset(_statement);

    public bool IsNull(int column) => SqliteNative.Functions.ColumnType(_statement, column) == SqliteNative.TypeNull;

    public long Int64(int column) => SqliteNative.Functions.ColumnInt64(_statement, column);

    public double? Double(int column) => IsNull(column) ? null : SqliteNative.Functions.ColumnDouble(_statement, column);

    public string? Text(int column)
    {
        if (IsNull(column))
        {
            return null;
        }

        var api = SqliteNative.Functions;
        var pointer = api.ColumnText(_statement, column);
        var length = api.ColumnBytes(_statement, column);
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer, length);
    }

    public void Dispose()
    {
        if (_statement != 0)
        {
            SqliteNative.Functions.Finalize(_statement);
            _statement = 0;
        }
    }
}
