using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Persistence;

public class UnitOfWork(DynamicDbContext db) : IUnitOfWork
{
    public Task CommitAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
