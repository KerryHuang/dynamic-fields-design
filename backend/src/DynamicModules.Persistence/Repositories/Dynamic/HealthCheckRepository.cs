using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class HealthCheckRepository(DynamicDbContext db) : IHealthCheckRepository
{
    private DbSet<HealthCheck> Checks => db.Set<HealthCheck>();
    private DbSet<HealthCheckResult> Results => db.Set<HealthCheckResult>();

    public Task<List<HealthCheck>> ListAsync(CancellationToken ct) => Checks.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);

    public Task<HealthCheck?> GetTrackedAsync(string code, CancellationToken ct) => Checks.FirstOrDefaultAsync(c => c.Code == code, ct);

    public Task<bool> ExistsAsync(string code, CancellationToken ct) => Checks.AnyAsync(c => c.Code == code, ct);

    public void Add(HealthCheck check) => Checks.Add(check);

    public async Task<bool> TouchProbeAsync(string code, DateTime nowUtc, CancellationToken ct) =>
        await Checks.Where(c => c.Code == code).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastProbeUtc, nowUtc), ct) == 1;

    public async Task<bool> LockAsync(string code, DateTime nowUtc, CancellationToken ct) =>
        await Checks.Where(c => c.Code == code).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastAggregatedUtc, nowUtc), ct) == 1;

    public Task<bool> ResultExistsAsync(string transitionId, CancellationToken ct) => Results.AnyAsync(r => r.TransitionId == transitionId, ct);

    public void AddResult(HealthCheckResult result) => Results.Add(result);

    public async Task<List<HealthCheckResult>> LatestPerInstanceAsync(string code, DateTime asOfUtc, DateTime sinceUtc, CancellationToken ct)
    {
        // 量很小（三個檢查間隔 × 實例數），讀出來在記憶體取各實例最新一筆
        var rows = await Results.AsNoTracking().Where(r => r.Code == code && r.CheckedUtc <= asOfUtc && r.CheckedUtc > sinceUtc).ToListAsync(ct);
        return [.. rows.GroupBy(r => r.InstanceId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.CheckedUtc).ThenByDescending(r => r.Id).First())
            .OrderBy(r => r.InstanceId, StringComparer.Ordinal)];
    }

    public async Task<bool> UpdateProjectionAsync(string code, DateTime asOfUtc, HealthStatus status, long? value, string? messageCode, CancellationToken ct) =>
        await Checks.Where(c => c.Code == code && (c.LastCheckedUtc == null || c.LastCheckedUtc <= asOfUtc)).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.LastCheckedUtc, asOfUtc)
            .SetProperty(c => c.LastStatus, status)
            .SetProperty(c => c.LastValue, value)
            .SetProperty(c => c.LastMessageCode, messageCode), ct) == 1;

    public Task AdvanceNextDueAsync(string code, DateTime nextDueUtc, CancellationToken ct) =>
        Checks.Where(c => c.Code == code && c.NextDueUtc < nextDueUtc).ExecuteUpdateAsync(s => s.SetProperty(c => c.NextDueUtc, nextDueUtc), ct);

    public Task<List<HealthCheckResult>> ResultsAsync(string code, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct) =>
        Results.AsNoTracking().Where(r => r.Code == code && r.CheckedUtc >= fromUtc && r.CheckedUtc <= toUtc)
            .OrderBy(r => r.CheckedUtc).ThenBy(r => r.Id).Take(take).ToListAsync(ct);

    public async Task<int> PurgeResultsBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var upper = await Results.Where(r => r.CheckedUtc < cutoffUtc).OrderBy(r => r.Id).Take(batchSize).Select(r => (long?)r.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Results.Where(r => r.CheckedUtc < cutoffUtc && r.Id <= max).ExecuteDeleteAsync(ct);
    }
}
