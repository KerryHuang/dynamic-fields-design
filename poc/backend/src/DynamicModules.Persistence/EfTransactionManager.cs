using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence;

public class EfTransactionManager(DynamicDbContext db) : ITransactionManager
{
    /// <summary>
    /// 已在交易內（如從範本建立時重播的內層指令）回不做事的 handle，由外層決定提交或回滾。
    /// 最外層交易先在 <see cref="WriteGate"/> 排隊，再開交易（EF 對 SQLite 開的是 BEGIN IMMEDIATE，一開始就持有寫入鎖）。
    /// </summary>
    public bool IsActive => db.Database.CurrentTransaction is not null;

    public async Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return NoopHandle.Instance;

        var connectionString = db.Database.GetConnectionString();
        var gate = WriteGate.For(connectionString);
        var permit = gate is null ? null : await gate.EnterAsync(WriteGate.TimeoutOf(connectionString), ct);
        try
        {
            return new Handle(await db.Database.BeginTransactionAsync(ct), permit);
        }
        catch (SqliteException ex) when (DatabaseBusy.Is(ex))
        {
            permit?.Dispose();
            throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
        }
        catch
        {
            permit?.Dispose();
            throw;
        }
    }

    private sealed class Handle(IDbContextTransaction tx, IDisposable? permit) : ITransactionScopeHandle
    {
        public async Task CommitAsync(CancellationToken ct)
        {
            try { await tx.CommitAsync(ct); }
            catch (SqliteException ex) when (DatabaseBusy.Is(ex))
            {
                throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
            }
        }

        public Task RollbackAsync(CancellationToken ct) => tx.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            try { await tx.DisposeAsync(); }
            finally { permit?.Dispose(); }
        }
    }

    private sealed class NoopHandle : ITransactionScopeHandle
    {
        public static readonly NoopHandle Instance = new();
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>SQLite 的「資料庫忙碌」錯誤碼：SQLITE_BUSY（5）、SQLITE_LOCKED（6）。</summary>
public static class DatabaseBusy
{
    public static bool Is(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqliteException { SqliteErrorCode: 5 or 6 } || e is DatabaseBusyException) return true;
        return false;
    }
}
