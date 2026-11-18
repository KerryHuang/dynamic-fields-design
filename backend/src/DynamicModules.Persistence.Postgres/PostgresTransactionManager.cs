using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的交易管理（PostgreSQL provider 設計 §5.6、§14 T18）：最外層交易先在 <see cref="WriteGate"/> 排隊（以連線字串為鍵、
/// 上限 <c>Database:LockTimeoutSeconds</c>），過了閘門才開連線與交易；交易開始時由 <see cref="PostgresWriteLockInterceptor"/> 取全庫鎖。
/// 每個實例因此最多一條連線在等 advisory lock，其餘在記憶體裡排隊、不占池連線。已在交易內回不做事的 handle，由外層決定提交或回滾。
/// </summary>
public class PostgresTransactionManager(DynamicDbContext db, PostgresWriteSettings settings) : ITransactionManager
{
    public bool IsActive => db.Database.CurrentTransaction is not null;

    public async Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return NoopHandle.Instance;

        var permit = await WriteGate.ForKey(PostgresSaveChangesGateInterceptor.GateKey(db)).EnterAsync(settings.LockTimeout, ct);
        try
        {
            return new Handle(await db.Database.BeginTransactionAsync(ct), permit);
        }
        catch (Exception ex) when (ex is not DatabaseBusyException && DatabaseBusy.Is(ex))
        {
            permit.Dispose();
            throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
        }
        catch
        {
            permit.Dispose();
            throw;
        }
    }

    private sealed class Handle(IDbContextTransaction tx, IDisposable permit) : ITransactionScopeHandle
    {
        public async Task CommitAsync(CancellationToken ct)
        {
            try { await tx.CommitAsync(ct); }
            catch (Exception ex) when (ex is not DatabaseBusyException && DatabaseBusy.Is(ex))
            {
                throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage, ex);
            }
        }

        public Task RollbackAsync(CancellationToken ct) => tx.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            try { await tx.DisposeAsync(); }
            finally { permit.Dispose(); }
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
