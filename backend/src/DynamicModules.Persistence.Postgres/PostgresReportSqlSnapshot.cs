using System.Data;
using System.Data.Common;
using System.Globalization;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Dynamic.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的報表讀取快照（PostgreSQL provider 設計 §5.9）：<c>REPEATABLE READ READ ONLY</c> 交易，第一個查詢固定快照；
/// 時間上限是 <c>SET LOCAL statement_timeout</c>，每個陳述式各自計時，到期時伺服器以 SQLSTATE 57014 中止，
/// 由 <see cref="Application.Common.IDatabaseErrors.IsQueryCanceled"/> 認得；<c>ct</c> 取消時 Npgsql 直接中斷執行中的陳述式。
/// <para>
/// 不經寫入閘門與全庫鎖（設計 §5.6 預留的判斷點）：交易直接在 <see cref="DbConnection"/> 上開，再用 <c>UseTransaction</c> 交給 EF。
/// EF 對外來交易只發 <c>TransactionUsed</c>、不發 <c>TransactionStarted</c>，而 <see cref="PostgresWriteLockInterceptor"/> 只在
/// <c>TransactionStarted</c> 取鎖；快照裡也沒有 SaveChanges，<see cref="PostgresSaveChangesGateInterceptor"/> 不會排隊。
/// </para>
/// 已有外層交易時（寫入流程裡呼叫）沿用它：包在 savepoint 裡設 statement_timeout，結束時回滾到 savepoint，
/// 逾時不會讓外層交易變成 aborted、設定也不會留到外層的其餘陳述式。
/// </summary>
public sealed class PostgresReportSqlSnapshot : IReportSqlSnapshot
{
    private const string Savepoint = "fw_report_snapshot";

    public async Task<string?> RunAsync(DynamicDbContext db, TimeSpan timeout, Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (db.Database.CurrentTransaction?.GetDbTransaction() is { } outer)
                return await InOuterTransactionAsync(connection, outer, timeout, body, ct);

            await using var own = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.UseTransactionAsync(own, ct);
            try
            {
                // 都在第一個查詢之前：READ ONLY 與 statement_timeout 只作用在這個交易
                await ExecuteAsync(connection, own, $"SET TRANSACTION READ ONLY; {StatementTimeout(timeout)}", ct);
                return await body(connection, own);
            }
            finally
            {
                // 唯讀交易不提交；Dispose 時回滾
                await db.Database.UseTransactionAsync(null, CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> InOuterTransactionAsync(
        DbConnection connection, DbTransaction outer, TimeSpan timeout, Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct)
    {
        await ExecuteAsync(connection, outer, $"SAVEPOINT {Savepoint}; {StatementTimeout(timeout)}", ct);
        string? reason;
        try
        {
            reason = await body(connection, outer);
        }
        catch
        {
            // 原本的例外優先；回滾失敗（例如連線已斷）時外層交易本來就救不回來
            try { await RestoreAsync(connection, outer); } catch { /* 見上 */ }
            throw;
        }
        await RestoreAsync(connection, outer);
        return reason;
    }

    /// <summary>回滾到 savepoint：撤銷 SET LOCAL、清掉錯誤狀態；快照裡沒有寫入，回滾不會丟資料。</summary>
    private static Task RestoreAsync(DbConnection connection, DbTransaction outer) =>
        ExecuteAsync(connection, outer, $"ROLLBACK TO SAVEPOINT {Savepoint}; RELEASE SAVEPOINT {Savepoint}", CancellationToken.None);

    /// <summary>statement_timeout 單位是毫秒、0 是不設上限，所以至少 1 毫秒（SQLite 版上限為 0 時同樣立刻中斷）。</summary>
    internal static string StatementTimeout(TimeSpan timeout)
    {
        var ms = Math.Max(1L, (long)Math.Ceiling(Math.Min(timeout.TotalMilliseconds, int.MaxValue)));
        return $"SET LOCAL statement_timeout = {ms.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
