using System.Data.Common;
using System.Net.Sockets;
using DynamicModules.Application.Common;
using Microsoft.Data.Sqlite;

namespace DynamicModules.Persistence;

/// <summary>
/// <see cref="IDatabaseErrors"/> 的實作，一份同時適用 SQLite 與 PostgreSQL：SQLite 看 <see cref="SqliteException"/> 的錯誤碼，
/// 其他資料庫看 .NET 標準的 <see cref="DbException.SqlState"/>，不引用 Npgsql 型別（PostgreSQL provider 設計 §5.7、§14 T11）。
/// 錯誤碼對照是可寫死的技術限制（設計 §11 第 3 項）。
/// </summary>
public sealed class DatabaseErrors : IDatabaseErrors
{
    public static readonly DatabaseErrors Instance = new();

    // SQLite 主錯誤碼與擴充錯誤碼
    private const int SqliteBusy = 5, SqliteLocked = 6, SqliteReadOnly = 8, SqliteInterrupt = 9, SqliteIoErr = 10, SqliteFull = 13,
        SqliteCantOpen = 14, SqliteConstraint = 19;
    // 三者的訊息都是 "UNIQUE constraint failed"（與改寫前比對訊息的判斷等價）；ROWID 是沒有 INTEGER PRIMARY KEY 的表 rowid 撞號
    private const int SqliteConstraintPrimaryKey = 1555, SqliteConstraintUnique = 2067, SqliteConstraintRowId = 2579;

    // PostgreSQL SQLSTATE
    private const string PgUniqueViolation = "23505", PgNumericOverflow = "22003", PgQueryCanceled = "57014",
        PgReadOnlyTransaction = "25006", PgDiskFull = "53100";
    private static readonly HashSet<string> PgUnavailable =
    [
        "57P01",   // admin_shutdown
        "57P02",   // crash_shutdown
        "57P03",   // cannot_connect_now（還沒能接受連線）
    ];
    private static readonly HashSet<string> PgBusyOrConflict =
    [
        "40001",   // serialization_failure
        "40P01",   // deadlock_detected
        "55P03",   // lock_not_available（lock_timeout）
    ];

    public bool IsUniqueViolation(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteConstraint
            && s.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey or SqliteConstraintRowId,
        DbException d => d.SqlState == PgUniqueViolation,
        _ => false,
    });

    public bool IsBusyOrConflict(Exception exception) => Any(exception, e => e switch
    {
        DatabaseBusyException => true,
        SqliteException s => s.SqliteErrorCode is SqliteBusy or SqliteLocked,
        // 取不到連線池的連線時 Npgsql 的例外不帶 SQLSTATE、內層是 TimeoutException（設計 §5.7；Npgsql 8 實測見 PostgresPoolExhaustionTests）
        DbException { SqlState: null, InnerException: TimeoutException } => true,
        DbException d => d.SqlState is { } state && PgBusyOrConflict.Contains(state),
        _ => false,
    });

    public bool IsNumericOverflow(Exception exception) => Any(exception, e => e switch
    {
        // SQLite 沒有溢位專屬的錯誤碼，只能看訊息；只剩這一處比對訊息
        SqliteException s => s.Message.Contains("integer overflow", StringComparison.OrdinalIgnoreCase),
        DbException d => d.SqlState == PgNumericOverflow,
        _ => false,
    });

    public bool IsQueryCanceled(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteInterrupt,
        DbException d => d.SqlState == PgQueryCanceled,
        _ => false,
    });

    public bool IsReadOnly(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteReadOnly,
        DbException d => d.SqlState == PgReadOnlyTransaction,
        _ => false,
    });

    public bool IsStorageFull(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteFull,
        DbException d => d.SqlState == PgDiskFull,
        _ => false,
    });

    public bool IsUnavailable(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode is SqliteCantOpen or SqliteIoErr,
        // 類別 08（connection exception）與伺服器關閉中、還沒能接受連線（維運設計 §17.2.9）
        DbException { SqlState: { } state } => state.StartsWith("08", StringComparison.Ordinal) || PgUnavailable.Contains(state),
        // Npgsql 在 socket 斷線、連不到主機時丟不帶 SQLSTATE 的例外，內層是 SocketException 或 IOException；
        // 連線池耗盡（內層 TimeoutException）已歸忙碌，不在這裡（o2b 實測：拒絕連線、硬殺後讀串流都落在這條，設計 §17.12）
        DbException { SqlState: null } d => HasInner(d, x => x is SocketException or IOException),
        // 資料庫主機名解析失敗：Npgsql 丟裸的 SocketException，看不出是不是資料庫，由 PostgreSQL 連線邊界包成這個型別（§17.12 審查修正）；
        // 裸的 SocketException 一律不歸資料庫（外部服務的 DNS 失敗照一般 500）
        DatabaseUnavailableException => true,
        _ => false,
    });

    private static bool HasInner(Exception exception, Func<Exception, bool> match)
    {
        for (var e = exception.InnerException; e is not null; e = e.InnerException)
            if (match(e)) return true;
        return false;
    }

    private static bool Any(Exception exception, Func<Exception, bool> match)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (match(e)) return true;
        return false;
    }
}
