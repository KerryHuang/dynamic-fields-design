using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

public class EfWorkSavepoints(DynamicDbContext db) : IWorkSavepoints
{
    public async Task<IWorkSavepoint> CreateAsync(CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("savepoint 必須在請求的交易內建立");
        var name = "sp_" + Guid.NewGuid().ToString("N");
        await tx.CreateSavepointAsync(name, ct);
        return new Handle(db, name);
    }

    private sealed class Handle(DynamicDbContext db, string name) : IWorkSavepoint
    {
        public async Task RollbackAsync(CancellationToken ct)
        {
            await db.Database.CurrentTransaction!.RollbackToSavepointAsync(name, ct);
            db.ChangeTracker.Clear();
        }

        public Task ReleaseAsync(CancellationToken ct) => db.Database.CurrentTransaction!.ReleaseSavepointAsync(name, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
