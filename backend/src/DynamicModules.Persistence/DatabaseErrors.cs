using System.Data.Common;
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
    private const int SqliteBusy = 5, SqliteLocked = 6, SqliteInterrupt = 9, SqliteConstraint = 19;
    private const int SqliteConstraintPrimaryKey = 1555, SqliteConstraintUnique = 2067;

    // PostgreSQL SQLSTATE
    private const string PgUniqueViolation = "23505", PgNumericOverflow = "22003", PgQueryCanceled = "57014";
    private static readonly HashSet<string> PgBusyOrConflict =
    [
        "40001",   // serialization_failure
        "40P01",   // deadlock_detected
        "55P03",   // lock_not_available（lock_timeout）
    ];

    public bool IsUniqueViolation(Exception exception) => Any(exception, e => e switch
    {
        SqliteException s => s.SqliteErrorCode == SqliteConstraint
            && s.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        DbException d => d.SqlState == PgUniqueViolation,
        _ => false,
    });

    public bool IsBusyOrConflict(Exception exception) => Any(exception, e => e switch
    {
        DatabaseBusyException => true,
        SqliteException s => s.SqliteErrorCode is SqliteBusy or SqliteLocked,
        // 取不到連線池的連線時 Npgsql 的例外不帶 SQLSTATE、內層是 TimeoutException（設計 §5.7，形狀待 pg0b 實測）
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

    private static bool Any(Exception exception, Func<Exception, bool> match)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (match(e)) return true;
        return false;
    }
}
