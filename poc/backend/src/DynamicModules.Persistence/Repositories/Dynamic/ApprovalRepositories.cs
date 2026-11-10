using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ApprovalFlowRepository(DynamicDbContext db) : IApprovalFlowRepository
{
    public Task<ApprovalFlow?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<ApprovalFlow>().FirstOrDefaultAsync(f => f.Key == key, ct);

    public Task<string?> ModuleKeyOfAsync(string flowKey, CancellationToken ct) =>
        (from f in db.Set<ApprovalFlow>().AsNoTracking()
         join m in db.Set<Module>().AsNoTracking() on f.ModuleId equals m.Id
         where f.Key == flowKey
         select m.Key).FirstOrDefaultAsync(ct);

    public async Task<List<FlowState>> ListCurrentByModuleAsync(long moduleId, CancellationToken ct)
    {
        var rows = await (
            from f in db.Set<ApprovalFlow>().AsNoTracking()
            join v in db.Set<ApprovalFlowVersion>().AsNoTracking() on f.CurrentVersionId equals v.Id
            where f.ModuleId == moduleId
            orderby f.ToOptionId, f.Priority
            select new { f, v.Definition }).ToListAsync(ct);
        return rows.Select(x => new FlowState(x.f, x.Definition)).ToList();
    }

    public Task<List<ApprovalFlow>> ListByTargetTrackedAsync(long moduleId, long toOptionId, CancellationToken ct) =>
        db.Set<ApprovalFlow>().Where(f => f.ModuleId == moduleId && f.ToOptionId == toOptionId).OrderBy(f => f.Priority).ToListAsync(ct);

    public async Task<List<ApprovalDefinition>> ListAllVersionDefinitionsAsync(long moduleId, CancellationToken ct,
        IReadOnlyCollection<long>? toOptionIds = null)
    {
        var flows = db.Set<ApprovalFlow>().AsNoTracking().Where(f => f.ModuleId == moduleId);
        if (toOptionIds is not null) flows = flows.Where(f => toOptionIds.Contains(f.ToOptionId));
        return await db.Set<ApprovalFlowVersion>().AsNoTracking()
            .Where(v => flows.Any(f => f.Id == v.FlowId))
            .OrderBy(v => v.FlowId).ThenBy(v => v.Version)
            .Select(v => v.Definition).ToListAsync(ct);
    }

    public Task<ApprovalFlowVersion?> GetVersionAsync(long versionId, CancellationToken ct) =>
        db.Set<ApprovalFlowVersion>().FirstOrDefaultAsync(v => v.Id == versionId, ct);

    public async Task<int> MaxVersionAsync(long flowId, CancellationToken ct) =>
        await db.Set<ApprovalFlowVersion>().Where(v => v.FlowId == flowId).MaxAsync(v => (int?)v.Version, ct) ?? 0;

    public async Task<bool> IsUsedAsync(long flowId, CancellationToken ct) =>
        await db.Set<ApprovalInstance>().AnyAsync(i => i.FlowId == flowId, ct)
        || await db.Set<ApprovalInstanceFlow>().AnyAsync(x => x.FlowId == flowId, ct);

    public async Task<bool> IsVersionUsedAsync(long versionId, CancellationToken ct) =>
        await db.Set<ApprovalInstance>().AnyAsync(i => i.FlowVersionId == versionId, ct)
        || await db.Set<ApprovalInstanceFlow>().AnyAsync(x => x.FlowVersionId == versionId, ct);

    public Task<int> CountPendingAsync(long flowId, CancellationToken ct) =>
        db.Set<ApprovalInstance>().CountAsync(i => i.FlowId == flowId && i.Status == ApprovalInstanceStatus.Pending, ct);

    public async Task<List<(long FlowId, string FlowKey, ApprovalDefinition Definition)>> ListDefinitionsInUseAsync(long moduleId, CancellationToken ct)
    {
        var flows = db.Set<ApprovalFlow>().AsNoTracking().Where(f => f.ModuleId == moduleId);
        return await InUse(flows, ct);
    }

    public async Task<List<string>> ListFlowKeysReferencingGroupAsync(long groupId, CancellationToken ct)
    {
        var rows = await InUse(db.Set<ApprovalFlow>().AsNoTracking(), ct);
        return rows.Where(x => References(x.Definition, groupId)).Select(x => x.FlowKey).Distinct().Order().ToList();
    }

    /// <summary>這些流程的目前版本，加上 pending 實例綁定或快照裡的版本。</summary>
    private async Task<List<(long FlowId, string FlowKey, ApprovalDefinition Definition)>> InUse(IQueryable<ApprovalFlow> flows, CancellationToken ct)
    {
        var pending = db.Set<ApprovalInstance>().AsNoTracking().Where(i => i.Status == ApprovalInstanceStatus.Pending);
        var bound = pending.Where(i => i.FlowVersionId != null).Select(i => i.FlowVersionId!.Value);
        var snapshot = db.Set<ApprovalInstanceFlow>().AsNoTracking().Where(x => pending.Any(i => i.Id == x.InstanceId)).Select(x => x.FlowVersionId);
        var rows = await (
            from v in db.Set<ApprovalFlowVersion>().AsNoTracking()
            join f in flows on v.FlowId equals f.Id
            where v.Id == f.CurrentVersionId || bound.Contains(v.Id) || snapshot.Contains(v.Id)
            orderby f.Key, v.Version
            select new { f.Id, f.Key, v.Definition }).ToListAsync(ct);
        return rows.Select(x => (x.Id, x.Key, x.Definition)).ToList();
    }

    private static bool References(ApprovalDefinition d, long groupId)
    {
        static bool In(ApproverSource? s, long g) =>
            s is not null && ((s.GroupIds?.Contains(g) ?? false) || (s.CandidateGroupIds?.Contains(g) ?? false));
        return In(d.FallbackApprovers, groupId) || (d.Stages ?? []).Any(s => In(s.Approvers, groupId));
    }

    public void Add(ApprovalFlow f) => db.Set<ApprovalFlow>().Add(f);
    public void AddVersion(ApprovalFlowVersion v) => db.Set<ApprovalFlowVersion>().Add(v);

    public async Task RemoveAsync(ApprovalFlow f, CancellationToken ct)
    {
        f.CurrentVersionId = null;
        foreach (var tracked in db.ChangeTracker.Entries<ApprovalFlowVersion>().Where(e => e.Entity.FlowId == f.Id).ToList())
            tracked.State = EntityState.Detached;
        await db.Set<ApprovalFlowVersion>().Where(v => v.FlowId == f.Id).ExecuteDeleteAsync(ct);
        db.Set<ApprovalFlow>().Remove(f);
    }
}
