using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence;

public class EfTransactionManager(DynamicDbContext db) : ITransactionManager
{
    /// <summary>已在交易內（如從範本建立時重播的內層指令）回不做事的 handle，由外層決定提交或回滾。</summary>
    public async Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct) =>
        db.Database.CurrentTransaction is not null
            ? NoopHandle.Instance
            : new Handle(await db.Database.BeginTransactionAsync(ct));

    private sealed class Handle(IDbContextTransaction tx) : ITransactionScopeHandle
    {
        public Task CommitAsync(CancellationToken ct) => tx.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct) => tx.RollbackAsync(ct);
        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }

    private sealed class NoopHandle : ITransactionScopeHandle
    {
        public static readonly NoopHandle Instance = new();
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
