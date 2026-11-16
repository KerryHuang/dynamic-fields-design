using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ReportRepository(DynamicDbContext db) : IReportRepository
{
    public Task<Report?> GetAsync(long id, CancellationToken ct) =>
        db.Set<Report>().Include(r => r.Shares).FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<List<Report>> ListVisibleAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken ct) =>
        db.Set<Report>().AsNoTracking().Include(r => r.Shares)
            .Where(r => r.OwnerUserId == userId || r.Shares.Any(s => groupIds.Contains(s.GroupId)))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

    public Task<List<Report>> ListSharedAsync(CancellationToken ct) =>
        db.Set<Report>().AsNoTracking().Include(r => r.Shares).Where(r => r.Shares.Any()).OrderBy(r => r.Id).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, CancellationToken ct) =>
        db.Set<Report>().AnyAsync(r => r.Name == name, ct);

    public void Add(Report report) => db.Set<Report>().Add(report);

    public void Remove(Report report) => db.Set<Report>().Remove(report);
}
