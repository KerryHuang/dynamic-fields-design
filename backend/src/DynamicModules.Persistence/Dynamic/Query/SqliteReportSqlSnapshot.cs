using System.Data.Common;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Persistence.Dynamic.Query;

/// <summary>
/// SQLite 的讀取快照：WAL 模式下延遲開始的交易在第一次讀取時固定快照，且不取寫入鎖。
/// 時間上限用 progress handler 執行：Microsoft.Data.Sqlite 的 CommandTimeout 只管等鎖，CancellationToken
/// 也中斷不了已在 sqlite3_step 裡的單一聚合語句；回傳非 0 時 SQLite 以 SQLITE_INTERRUPT（9）中止該語句。
/// <para>
/// 探測與逐筆條件以 INDEXED BY 指定索引（<see cref="Dialects.SqliteDialect.IndexHint"/>）；實際 schema 沒有該索引（不是由 migration 建出的檔、
/// 索引改名）或規劃器用不上時，SQLite 在 prepare 就丟錯。這種情形回 <c>indexMissing:</c> 退回記憶體路徑並記 Warning，不讓整張報表變成 500。
/// </para>
/// </summary>
public sealed class SqliteReportSqlSnapshot(ILogger<SqliteReportSqlSnapshot> logger) : IReportSqlSnapshot
{
    /// <summary>每執行這麼多個 VM 指令檢查一次時間上限。</summary>
    private const int ProgressInstructions = 10_000;

    public async Task<string?> RunAsync(DynamicDbContext db, TimeSpan timeout, Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        SqliteTransaction? own = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, ProgressInstructions,
            _ => clock.Elapsed > timeout || ct.IsCancellationRequested ? 1 : 0, null);
        try
        {
            var tx = db.Database.CurrentTransaction?.GetDbTransaction();
            if (tx is null)
            {
                own = connection.BeginTransaction(deferred: true);
                tx = own;
                await db.Database.UseTransactionAsync(own, ct);
            }
            return await body(connection, tx);
        }
        catch (SqliteException e) when (e.Message.Contains("no such index") || e.Message.Contains("no query solution"))
        {
            const string marker = "no such index: ";
            var at = e.Message.IndexOf(marker, StringComparison.Ordinal);
            var name = at < 0 ? "unknown" : e.Message[(at + marker.Length)..].TrimEnd('\'', '.', ' ');
            logger.LogWarning(e, "報表 SQL 路徑找不到指定的索引 {Index}，改走記憶體路徑", name);
            return $"indexMissing:{name}";
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            if (own is not null)
            {
                await db.Database.UseTransactionAsync(null, ct);
                await own.DisposeAsync();
            }
            await db.Database.CloseConnectionAsync();
        }
    }
}
