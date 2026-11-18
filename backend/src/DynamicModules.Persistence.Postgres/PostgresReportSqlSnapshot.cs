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
/// 用戶端的 Command Timeout 在快照範圍內放寬到比 statement_timeout 長（<see cref="ClientTimeoutSeconds"/>），結束時還原，
/// 讓逾時一律由伺服器端的 57014 決定。
/// <para>
/// 已有外層交易時（寫入流程裡呼叫）沿用它：包在 savepoint 裡設 statement_timeout，結束時回滾到 savepoint，
/// 逾時不會讓外層交易變成 aborted、設定也不會留到外層的其餘陳述式。
/// </para>
/// </summary>
public sealed class PostgresReportSqlSnapshot : IReportSqlSnapshot
{
    private const string Savepoint = "fw_report_snapshot";

    public async Task<string?> RunAsync(DynamicDbContext db, TimeSpan timeout, Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var clientTimeout = ClientTimeoutSeconds(timeout);
        var previousTimeout = db.Database.GetCommandTimeout();
        // 快照內的 EF 查詢用這個值；Dapper 命令照 IReportSqlSnapshot 的約定讀 GetCommandTimeout() 帶進去
        db.Database.SetCommandTimeout(clientTimeout);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                if (db.Database.CurrentTransaction?.GetDbTransaction() is { } outer)
                    return await InOuterTransactionAsync(connection, outer, timeout, clientTimeout, body, ct);

                await using var own = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                // 交易包裝 Dispose 時解除關聯，並平衡 UseTransaction 加的連線開啟計數（UseTransaction(null) 只解除關聯）；先於 own 釋放
                await using var _ = await db.Database.UseTransactionAsync(own, ct);
                // 都在第一個查詢之前：READ ONLY 與 statement_timeout 只作用在這個交易
                await ExecuteAsync(connection, own, $"SET TRANSACTION READ ONLY; {StatementTimeout(timeout)}", clientTimeout, ct);
                // 唯讀交易不提交；Dispose 時回滾
                return await body(connection, own);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    private static async Task<string?> InOuterTransactionAsync(
        DbConnection connection, DbTransaction outer, TimeSpan timeout, int clientTimeout,
        Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct)
    {
        await ExecuteAsync(connection, outer, $"SAVEPOINT {Savepoint}; {StatementTimeout(timeout)}", clientTimeout, ct);
        string? reason;
        try
        {
            reason = await body(connection, outer);
        }
        catch
        {
            // 原本的例外優先；回滾失敗（例如連線已斷）時外層交易本來就救不回來
            try { await RestoreAsync(connection, outer, clientTimeout); } catch { /* 見上 */ }
            throw;
        }
        await RestoreAsync(connection, outer, clientTimeout);
        return reason;
    }

    /// <summary>回滾到 savepoint：撤銷 SET LOCAL、清掉錯誤狀態；快照裡沒有寫入，回滾不會丟資料。</summary>
    private static Task RestoreAsync(DbConnection connection, DbTransaction outer, int clientTimeout) =>
        ExecuteAsync(connection, outer, $"ROLLBACK TO SAVEPOINT {Savepoint}; RELEASE SAVEPOINT {Savepoint}", clientTimeout, CancellationToken.None);

    /// <summary>statement_timeout 單位是毫秒、0 是不設上限，所以至少 1 毫秒（SQLite 版上限為 0 時同樣立刻中斷）。</summary>
    internal static string StatementTimeout(TimeSpan timeout)
    {
        var ms = Math.Max(1L, (long)Math.Ceiling(Math.Min(timeout.TotalMilliseconds, int.MaxValue)));
        return $"SET LOCAL statement_timeout = {ms.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 用戶端的 Command Timeout（秒）：比 statement_timeout 多留餘裕，讓伺服器端先以 57014 中止。
    /// Npgsql 預設 30 秒（連線字串的 Command Timeout）；比報表上限短時會先在用戶端逾時，丟不帶 SQLSTATE、內層 TimeoutException 的例外，
    /// 被當成忙碌回 503，而不是「報表執行逾時」。
    /// </summary>
    internal static int ClientTimeoutSeconds(TimeSpan timeout) =>
        (int)Math.Ceiling(Math.Clamp(timeout.TotalSeconds, 0, int.MaxValue / 1000.0)) + ClientTimeoutMarginSeconds;

    private const int ClientTimeoutMarginSeconds = 5;

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction tx, string sql, int clientTimeout, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = clientTimeout;
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
