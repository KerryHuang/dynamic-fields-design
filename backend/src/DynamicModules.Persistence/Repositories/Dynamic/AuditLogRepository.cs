using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class AuditLogRepository(DynamicDbContext db) : IAuditLogRepository
{
    public void Add(AuditLogEntry entry) => db.Set<AuditLogEntry>().Add(entry);

    public Task<List<AuditLogEntry>> PageAsync(AuditLogQuery query, int take, CancellationToken ct)
    {
        var q = db.Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.OccurredUtc >= query.FromUtc && a.OccurredUtc <= query.ToUtc);
        if (query.BeforeId is { } before) q = q.Where(a => a.Id < before);
        if (query.ActorUserId is { } actor) q = q.Where(a => a.ActorUserId == actor);
        if (query.Actions is { } actions) q = q.Where(a => actions.Contains(a.Action));
        if (query.Channel is { } channel) q = q.Where(a => a.Channel == channel);
        if (query.Result is { } result) q = q.Where(a => a.Result == result);
        if (query.TargetKind is { } kind) q = q.Where(a => a.TargetKind == kind);
        if (query.TargetId is { } id) q = q.Where(a => a.TargetId == id);
        if (query.TokenId is { } tokenId) q = q.Where(a => a.TokenId == tokenId);
        return q.OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
    }

    public Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct) =>
        db.Set<AuditLogEntry>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        // Id 上界：這一批最舊的 batchSize 列裡最大的 Id；刪除條件同時帶時間，Id 晚於上界或時間未到的列都不會被刪（§17.2.8）
        var upper = await db.Set<AuditLogEntry>().Where(a => a.OccurredUtc < cutoffUtc)
            .OrderBy(a => a.Id).Take(batchSize).Select(a => (long?)a.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await db.Set<AuditLogEntry>().Where(a => a.OccurredUtc < cutoffUtc && a.Id <= max).ExecuteDeleteAsync(ct);
    }

    public Task<int> CountSinceAsync(string action, DateTime sinceUtc, CancellationToken ct) =>
        db.Set<AuditLogEntry>().CountAsync(a => a.Action == action && a.OccurredUtc >= sinceUtc, ct);

    public async Task<int> CountForTokenSinceAsync(long tokenId, DateTime sinceUtc, IReadOnlyCollection<string> actions, CancellationToken ct)
    {
        if (actions.Count == 0) return 0;
        return await db.Set<AuditLogEntry>().CountAsync(a => a.TokenId == tokenId && a.OccurredUtc >= sinceUtc && actions.Contains(a.Action), ct);
    }

    public async Task<Dictionary<long, int>> CountForTokensSinceAsync(
        IReadOnlyCollection<long> tokenIds, DateTime sinceUtc, IReadOnlyCollection<string> actions, CancellationToken ct)
    {
        if (tokenIds.Count == 0 || actions.Count == 0) return [];
        var ids = tokenIds.Select(id => (long?)id).ToList();
        return await db.Set<AuditLogEntry>()
            .Where(a => ids.Contains(a.TokenId) && a.OccurredUtc >= sinceUtc && actions.Contains(a.Action))
            .GroupBy(a => a.TokenId!.Value)
            .Select(g => new { TokenId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TokenId, x => x.Count, ct);
    }
}
