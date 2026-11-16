using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Domain.Data;

namespace DynamicModules.Persistence.Seed;

public class DemoPeopleSeeder(DynamicDbContext db, IBusinessTime time) : IDemoPeople
{
    public Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct) => DemoPeople.ConflictsAsync(db, ct);

    public async Task AddAsync(CancellationToken ct)
    {
        await DemoPeople.EnsureAsync(db, ct);
        await DemoPeople.EnsureOrganizationAsync(db, time.Now, ct);
    }
}
