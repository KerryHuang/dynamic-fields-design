using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Domain.Data;

namespace DynamicModules.Persistence.Seed;

public class DemoPeopleSeeder(DynamicDbContext db) : IDemoPeople
{
    public Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct) => DemoPeople.ConflictsAsync(db, ct);

    public Task AddAsync(CancellationToken ct) => DemoPeople.EnsureAsync(db, ct);
}
