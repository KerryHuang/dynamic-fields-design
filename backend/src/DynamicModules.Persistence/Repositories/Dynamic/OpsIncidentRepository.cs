using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class OpsIncidentRepository(DynamicDbContext db) : IOpsIncidentRepository
{
    private IQueryable<OpsIncident> Incidents => db.Set<OpsIncident>();

    public Task<OpsIncident?> FindByFingerprintAsync(string fingerprint, CancellationToken ct) =>
        Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Fingerprint == fingerprint, ct);

    public Task<OpsIncident?> GetAsync(long id, CancellationToken ct) => Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

    public void Add(OpsIncident incident) => db.Set<OpsIncident>().Add(incident);

    public void Detach(OpsIncident incident) => db.Entry(incident).State = EntityState.Detached;

    public async Task<bool> TryBumpAsync(long id, IncidentStatus expected, IncidentBump b, bool reopen, CancellationToken ct)
    {
        var q = Incidents.Where(i => i.Id == id && i.Status == expected);
        // 升級退回開啟時清掉確認（§17.2.2）；分兩句寫，不在 SET 裡用條件運算式包轉換過的列舉
        return reopen
            ? await q.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.OccurrenceCount, i => i.OccurrenceCount + b.Count)
                .SetProperty(i => i.LastSeenUtc, i => i.LastSeenUtc > b.SeenUtc ? i.LastSeenUtc : b.SeenUtc)
                .SetProperty(i => i.LastVersion, b.Version)
                .SetProperty(i => i.LastRequestId, i => b.RequestId ?? i.LastRequestId)
                .SetProperty(i => i.Severity, b.Severity)
                .SetProperty(i => i.TitleParamsJson, i => b.TitleParamsJson ?? i.TitleParamsJson)
                .SetProperty(i => i.Status, IncidentStatus.Open)
                .SetProperty(i => i.AcknowledgedBy, (string?)null)
                .SetProperty(i => i.AcknowledgedUtc, (DateTime?)null), ct) == 1
            : await q.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.OccurrenceCount, i => i.OccurrenceCount + b.Count)
                .SetProperty(i => i.LastSeenUtc, i => i.LastSeenUtc > b.SeenUtc ? i.LastSeenUtc : b.SeenUtc)
                .SetProperty(i => i.LastVersion, b.Version)
                .SetProperty(i => i.LastRequestId, i => b.RequestId ?? i.LastRequestId)
                .SetProperty(i => i.Severity, b.Severity)
                .SetProperty(i => i.TitleParamsJson, i => b.TitleParamsJson ?? i.TitleParamsJson), ct) == 1;
    }

    public async Task<bool> TryRegressAsync(long id, IncidentBump b, CancellationToken ct) =>
        await Incidents.Where(i => i.Id == id && i.Status == IncidentStatus.Resolved).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, IncidentStatus.Open)
            .SetProperty(i => i.Regression, true)
            .SetProperty(i => i.ReopenCount, i => i.ReopenCount + 1)
            .SetProperty(i => i.OccurrenceCount, i => i.OccurrenceCount + b.Count)
            .SetProperty(i => i.LastSeenUtc, i => i.LastSeenUtc > b.SeenUtc ? i.LastSeenUtc : b.SeenUtc)
            .SetProperty(i => i.LastVersion, b.Version)
            .SetProperty(i => i.LastRequestId, i => b.RequestId ?? i.LastRequestId)
            .SetProperty(i => i.Severity, b.Severity)
            .SetProperty(i => i.TitleParamsJson, i => b.TitleParamsJson ?? i.TitleParamsJson)
            .SetProperty(i => i.AcknowledgedBy, (string?)null)
            .SetProperty(i => i.AcknowledgedUtc, (DateTime?)null)
            .SetProperty(i => i.ResolvedBy, (string?)null)
            .SetProperty(i => i.ResolvedUtc, (DateTime?)null)
            .SetProperty(i => i.ResolvedReason, (IncidentResolvedReason?)null), ct) == 1;

    public async Task<bool> TryResolveAsync(long id, IncidentStatus expected, IncidentResolvedReason reason, string? by, DateTime atUtc, CancellationToken ct) =>
        await Incidents.Where(i => i.Id == id && i.Status == expected).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, IncidentStatus.Resolved)
            .SetProperty(i => i.ResolvedReason, reason)
            .SetProperty(i => i.ResolvedBy, by)
            .SetProperty(i => i.ResolvedUtc, atUtc), ct) == 1;

    public async Task<bool> TryAcknowledgeAsync(long id, string by, DateTime atUtc, CancellationToken ct) =>
        await Incidents.Where(i => i.Id == id && i.Status == IncidentStatus.Open).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, IncidentStatus.Acknowledged)
            .SetProperty(i => i.AcknowledgedBy, by)
            .SetProperty(i => i.AcknowledgedUtc, atUtc), ct) == 1;

    public Task MarkNotifiedAsync(long id, DateTime atUtc, CancellationToken ct) =>
        Incidents.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastNotifiedUtc, atUtc), ct);

    public async Task<bool> TryClaimRenotifyAsync(long id, DateTime cutoffUtc, DateTime nowUtc, CancellationToken ct) =>
        await Incidents.Where(i => i.Id == id && i.Status == IncidentStatus.Open && i.Severity == IncidentSeverity.Critical
                && (i.LastNotifiedUtc == null || i.LastNotifiedUtc < cutoffUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastNotifiedUtc, nowUtc), ct) == 1;

    public Task<List<long>> ListRenotifyCandidatesAsync(DateTime cutoffUtc, int take, CancellationToken ct) =>
        Incidents.AsNoTracking()
            .Where(i => i.Status == IncidentStatus.Open && i.Severity == IncidentSeverity.Critical && (i.LastNotifiedUtc == null || i.LastNotifiedUtc < cutoffUtc))
            .OrderBy(i => i.Id).Take(take).Select(i => i.Id).ToListAsync(ct);

    public async Task<int> ExpireBatchAsync(IReadOnlyCollection<IncidentSource> sources, IReadOnlyCollection<string> excludeCodes, DateTime cutoffUtc,
        DateTime nowUtc, int batchSize, CancellationToken ct)
    {
        var ids = await Incidents.AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Resolved && sources.Contains(i.Source) && !excludeCodes.Contains(i.Code) && i.LastSeenUtc < cutoffUtc)
            .OrderBy(i => i.Id).Take(batchSize).Select(i => i.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        // 條件再帶一次：讀完到更新之間又出現的（LastSeenUtc 被推後）不結案
        return await Incidents.Where(i => ids.Contains(i.Id) && i.Status != IncidentStatus.Resolved && i.LastSeenUtc < cutoffUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IncidentStatus.Resolved)
                .SetProperty(i => i.ResolvedReason, IncidentResolvedReason.Expired)
                .SetProperty(i => i.ResolvedBy, (string?)null)
                .SetProperty(i => i.ResolvedUtc, nowUtc), ct);
    }

    public void AddSample(OpsIncidentSample sample) => db.Set<OpsIncidentSample>().Add(sample);

    public async Task TrimSamplesAsync(long incidentId, int keep, CancellationToken ct)
    {
        var stale = await db.Set<OpsIncidentSample>().AsNoTracking().Where(s => s.IncidentId == incidentId)
            .OrderByDescending(s => s.OccurredUtc).ThenByDescending(s => s.Id).Skip(keep).Select(s => s.Id).ToListAsync(ct);
        if (stale.Count > 0) await db.Set<OpsIncidentSample>().Where(s => stale.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }

    public Task<List<OpsIncidentSample>> ListSamplesAsync(long incidentId, CancellationToken ct) =>
        db.Set<OpsIncidentSample>().AsNoTracking().Where(s => s.IncidentId == incidentId)
            .OrderByDescending(s => s.OccurredUtc).ThenByDescending(s => s.Id).ToListAsync(ct);

    public Task<List<OpsIncident>> PageAsync(OpsIncidentQuery query, int take, CancellationToken ct)
    {
        var q = Incidents.AsNoTracking();
        if (query.Statuses is { Count: > 0 } statuses) q = q.Where(i => statuses.Contains(i.Status));
        if (query.Category is { } category) q = q.Where(i => i.Category == category);
        if (query.Severity is { } severity) q = q.Where(i => i.Severity == severity);
        if (query.BeforeId is { } before) q = q.Where(i => i.Id < before);
        return q.OrderByDescending(i => i.Id).Take(take).ToListAsync(ct);
    }

    public Task<List<OpsIncident>> ListUnresolvedAsync(CancellationToken ct) =>
        Incidents.AsNoTracking().Where(i => i.Status != IncidentStatus.Resolved).OrderBy(i => i.Id).ToListAsync(ct);

    public async Task<int> PurgeResolvedBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var upper = await Incidents.Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedUtc < cutoffUtc)
            .OrderBy(i => i.Id).Take(batchSize).Select(i => (long?)i.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Incidents.Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedUtc < cutoffUtc && i.Id <= max).ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeSamplesBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var samples = db.Set<OpsIncidentSample>();
        var upper = await samples.Where(s => s.OccurredUtc < cutoffUtc).OrderBy(s => s.Id).Take(batchSize).Select(s => (long?)s.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await samples.Where(s => s.OccurredUtc < cutoffUtc && s.Id <= max).ExecuteDeleteAsync(ct);
    }
}
