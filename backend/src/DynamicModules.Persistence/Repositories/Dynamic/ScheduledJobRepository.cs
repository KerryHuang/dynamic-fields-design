using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ScheduledJobRepository(DynamicDbContext db) : IScheduledJobRepository
{
    public Task<List<ScheduledJob>> ListAsync(CancellationToken ct) =>
        db.Set<ScheduledJob>().AsNoTracking().OrderBy(j => j.JobKey).ToListAsync(ct);

    public Task<bool> ExistsAsync(string jobKey, CancellationToken ct) =>
        db.Set<ScheduledJob>().AnyAsync(j => j.JobKey == jobKey, ct);

    public void Add(ScheduledJob job) => db.Set<ScheduledJob>().Add(job);

    public async Task<bool> TryAcquireAsync(string jobKey, string owner, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct) =>
        await db.Set<ScheduledJob>()
            .Where(j => j.JobKey == jobKey && j.NextDueUtc <= nowUtc && (j.LeaseOwner == null || j.LeaseUntilUtc < nowUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, owner)
                .SetProperty(j => j.LeaseUntilUtc, leaseUntilUtc)
                .SetProperty(j => j.LastStartedUtc, nowUtc), ct) == 1;

    public async Task<bool> RenewAsync(string jobKey, string owner, DateTime leaseUntilUtc, CancellationToken ct) =>
        await db.Set<ScheduledJob>().Where(j => j.JobKey == jobKey && j.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntilUtc, leaseUntilUtc), ct) == 1;

    public async Task<bool> FinishAsync(string jobKey, string owner, JobRunResult result, CancellationToken ct)
    {
        var error = result.Error is { Length: > ScheduledJob.LastErrorMax } e ? e[..ScheduledJob.LastErrorMax] : result.Error;
        // 連續失敗 = 舊值 × keep + add：成功歸零，取消不動，失敗與部分失敗加一
        var (keep, add) = result.Status switch
        {
            ScheduledJobStatus.Succeeded => (0, 0),
            ScheduledJobStatus.Cancelled => (1, 0),
            _ => (1, 1),
        };
        return await db.Set<ScheduledJob>().Where(j => j.JobKey == jobKey && j.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LastFinishedUtc, result.FinishedUtc)
                .SetProperty(j => j.LastStatus, result.Status)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.NextDueUtc, result.NextDueUtc)
                .SetProperty(j => j.ConsecutiveFailures, j => j.ConsecutiveFailures * keep + add)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)null), ct) == 1;
    }
}
