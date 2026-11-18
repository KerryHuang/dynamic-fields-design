using System.Data.Common;
using DynamicModules.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// 全庫寫入鎖（PostgreSQL provider 設計 §5.6、§14 T3）：EF 開始的每個交易，第一個動作是 <c>SET LOCAL lock_timeout</c>，
/// 再 <c>pg_advisory_xact_lock</c>，交易結束自動釋放。隔離層級維持 Read Committed：鎖是 BEGIN 後的第一個陳述式，
/// 持鎖者一定看得到所有先前寫入者提交的結果，效果等同 SQLite 的 BEGIN IMMEDIATE。
/// 取鎖失敗時 EF 還沒把交易設成 CurrentTransaction，這裡自己回滾、釋放，再丟例外（逾時 55P03 轉成 <see cref="DatabaseBusyException"/>）。
/// pg1 的報表唯讀快照若經 EF 開交易，要在這裡跳過（判斷方式在 pg1 定）。
/// </summary>
public sealed class PostgresWriteLockInterceptor(PostgresWriteSettings settings) : DbTransactionInterceptor
{
    private string Sql
    {
        get
        {
            // lock_timeout 單位是毫秒，0 是不設上限
            var ms = settings.LockTimeoutSeconds <= 0 ? 0L : settings.LockTimeoutSeconds * 1000L;
            return $"SET LOCAL lock_timeout = {ms}; SELECT pg_advisory_xact_lock({PostgresLockKeys.Write});";
        }
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        if (!settings.Serialized) return result;
        try
        {
            using var command = Command(connection, result);
            command.ExecuteNonQuery();
            return result;
        }
        catch (Exception ex)
        {
            Abandon(result);
            if (ex is not DatabaseBusyException && DatabaseBusy.Is(ex)) throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
            throw;
        }
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        if (!settings.Serialized) return result;
        try
        {
            await using var command = Command(connection, result);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await AbandonAsync(result);
            if (ex is not DatabaseBusyException && DatabaseBusy.Is(ex)) throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
            throw;
        }
    }

    private DbCommand Command(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        // 等鎖由 lock_timeout 把關；指令逾時不設上限，避免比 lock_timeout 先到而變成非忙碌的錯誤
        command.CommandTimeout = 0;
        return command;
    }

    private static void Abandon(DbTransaction transaction)
    {
        try { transaction.Rollback(); } catch { /* 連線已斷時回滾也會失敗；交易隨連線結束 */ }
        transaction.Dispose();
    }

    private static async Task AbandonAsync(DbTransaction transaction)
    {
        try { await transaction.RollbackAsync(); } catch { /* 同上 */ }
        await transaction.DisposeAsync();
    }
}
