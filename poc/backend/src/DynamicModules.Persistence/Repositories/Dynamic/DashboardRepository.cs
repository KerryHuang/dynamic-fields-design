using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class DashboardRepository(DynamicDbContext db) : IDashboardRepository
{
    public Task<Dashboard?> GetAsync(long id, CancellationToken ct) =>
        db.Set<Dashboard>().Include(d => d.Assignments).FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<List<Dashboard>> ListAssignedToAsync(IReadOnlyCollection<long> groupIds, CancellationToken ct) =>
        db.Set<Dashboard>().AsNoTracking().Include(d => d.Assignments)
            .Where(d => d.OwnerUserId == null && d.Assignments.Any(a => groupIds.Contains(a.GroupId)))
            .OrderBy(d => d.Sequence).ThenBy(d => d.Id).ToListAsync(ct);

    public Task<List<Dashboard>> ListAssignedAsync(CancellationToken ct) =>
        db.Set<Dashboard>().AsNoTracking().Include(d => d.Assignments)
            .Where(d => d.OwnerUserId == null).OrderBy(d => d.Sequence).ThenBy(d => d.Id).ToListAsync(ct);

    public Task<List<Dashboard>> ListOwnedAsync(string userId, CancellationToken ct) =>
        db.Set<Dashboard>().AsNoTracking().Where(d => d.OwnerUserId == userId).OrderBy(d => d.Id).ToListAsync(ct);

    public Task<List<DashboardPersonalVersion>> ListPersonalAsync(string userId, IReadOnlyCollection<long> dashboardIds, CancellationToken ct) =>
        db.Set<DashboardPersonalVersion>().AsNoTracking().Where(p => p.UserId == userId && dashboardIds.Contains(p.DashboardId)).ToListAsync(ct);

    public Task<DashboardPersonalVersion?> GetPersonalAsync(long dashboardId, string userId, CancellationToken ct) =>
        db.Set<DashboardPersonalVersion>().FirstOrDefaultAsync(p => p.DashboardId == dashboardId && p.UserId == userId, ct);

    public async Task<int> MaxSequenceAsync(CancellationToken ct) =>
        await db.Set<Dashboard>().Where(d => d.OwnerUserId == null).MaxAsync(d => (int?)d.Sequence, ct) ?? 0;

    public Task<bool> AssignedNameExistsAsync(string name, CancellationToken ct) =>
        db.Set<Dashboard>().AnyAsync(d => d.OwnerUserId == null && d.Name == name, ct);

    // 版面是 JSON，沒有可靠的 SQL 查法（不假設格式化方式），逐筆解析；儀表板與個人版的數量遠小於記錄數
    public async Task<List<DashboardReportReference>> ListReferencesAsync(long reportId, CancellationToken ct)
    {
        var refs = new List<DashboardReportReference>();
        var dashboards = await db.Set<Dashboard>().AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
        refs.AddRange(dashboards.Where(d => References(d.LayoutJson, reportId))
            .Select(d => new DashboardReportReference(d.Id, d.Name, d.OwnerUserId, false, null)));
        var names = dashboards.ToDictionary(d => d.Id, d => d.Name);
        var personal = await db.Set<DashboardPersonalVersion>().AsNoTracking().OrderBy(p => p.DashboardId).ThenBy(p => p.UserId).ToListAsync(ct);
        refs.AddRange(personal.Where(p => References(p.LayoutJson, reportId))
            .Select(p => new DashboardReportReference(p.DashboardId, names.GetValueOrDefault(p.DashboardId, ""), null, true, p.UserId)));
        return refs;
    }

    public async Task<(List<Dashboard> Owned, List<DashboardPersonalVersion> Personal)> ListOwnReferencingTrackedAsync(
        long reportId, string userId, CancellationToken ct)
    {
        var owned = (await db.Set<Dashboard>().Where(d => d.OwnerUserId == userId).ToListAsync(ct))
            .Where(d => References(d.LayoutJson, reportId)).ToList();
        var personal = (await db.Set<DashboardPersonalVersion>().Where(p => p.UserId == userId).ToListAsync(ct))
            .Where(p => References(p.LayoutJson, reportId)).ToList();
        return (owned, personal);
    }

    public void Add(Dashboard dashboard) => db.Set<Dashboard>().Add(dashboard);

    public void AddPersonal(DashboardPersonalVersion version) => db.Set<DashboardPersonalVersion>().Add(version);

    public void RemovePersonal(DashboardPersonalVersion version) => db.Set<DashboardPersonalVersion>().Remove(version);

    public async Task RemoveAsync(Dashboard dashboard, CancellationToken ct)
    {
        db.Set<DashboardPersonalVersion>().RemoveRange(await db.Set<DashboardPersonalVersion>().Where(p => p.DashboardId == dashboard.Id).ToListAsync(ct));
        db.Set<Dashboard>().Remove(dashboard);
    }

    private static bool References(string layoutJson, long reportId) => DashboardLayout.Parse(layoutJson).ReportIds().Contains(reportId);
}
