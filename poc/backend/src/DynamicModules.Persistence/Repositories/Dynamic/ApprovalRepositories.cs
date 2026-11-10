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

public class ApprovalInstanceRepository(DynamicDbContext db) : IApprovalInstanceRepository
{
    public Task<ApprovalInstance?> GetAsync(long id, CancellationToken ct) =>
        db.Set<ApprovalInstance>().FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<ApprovalInstance?> GetPendingByRecordAsync(long recordId, CancellationToken ct) =>
        db.Set<ApprovalInstance>().FirstOrDefaultAsync(i => i.RecordId == recordId && i.Status == ApprovalInstanceStatus.Pending, ct);

    public async Task<HashSet<long>> ListPendingRecordIdsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        if (recordIds.Count == 0) return [];
        var ids = await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.Status == ApprovalInstanceStatus.Pending && recordIds.Contains(i.RecordId)).Select(i => i.RecordId).ToListAsync(ct);
        // 同一請求內新建、尚未存檔的不會在這裡；呼叫端另外合併
        return [.. ids];
    }

    public async Task<bool> TryBumpRowVersionAsync(long instanceId, string expected, string next, CancellationToken ct)
    {
        if (await db.Set<ApprovalInstance>().Where(i => i.Id == instanceId && i.RowVersion == expected)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.RowVersion, next), ct) != 1) return false;
        // 已追蹤的實例同步新值，之後的 SaveChanges 以新值當並行權杖
        if (db.Set<ApprovalInstance>().Local.FirstOrDefault(i => i.Id == instanceId) is { } tracked)
        {
            var p = db.Entry(tracked).Property(i => i.RowVersion);
            p.CurrentValue = next;
            p.OriginalValue = next;
        }
        return true;
    }

    public Task<List<ApprovalStageRun>> ListRunsAsync(long instanceId, CancellationToken ct) =>
        db.Set<ApprovalStageRun>().Where(r => r.InstanceId == instanceId).OrderBy(r => r.Seq).ToListAsync(ct);

    public Task<List<ApprovalTask>> ListTasksAsync(long instanceId, CancellationToken ct) =>
        db.Set<ApprovalTask>().Where(t => t.InstanceId == instanceId).OrderBy(t => t.Id).ToListAsync(ct);

    public Task<ApprovalTask?> GetTaskAsync(long taskId, CancellationToken ct) =>
        db.Set<ApprovalTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct);

    public Task<List<ApprovalInstance>> ListByRecordAsync(long recordId, CancellationToken ct) =>
        db.Set<ApprovalInstance>().AsNoTracking().Where(i => i.RecordId == recordId)
            .OrderByDescending(i => i.SubmittedTime).ThenByDescending(i => i.Id).ToListAsync(ct);

    public Task<List<ApprovalAction>> ListActionsAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct) =>
        db.Set<ApprovalAction>().AsNoTracking().Where(a => instanceIds.Contains(a.InstanceId)).OrderBy(a => a.Id).ToListAsync(ct);

    public Task<List<ApprovalStageRun>> ListRunsAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct) =>
        db.Set<ApprovalStageRun>().AsNoTracking().Where(r => instanceIds.Contains(r.InstanceId)).OrderBy(r => r.InstanceId).ThenBy(r => r.Seq).ToListAsync(ct);

    public Task<List<ApprovalTask>> ListTasksAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct) =>
        db.Set<ApprovalTask>().AsNoTracking().Where(t => instanceIds.Contains(t.InstanceId)).OrderBy(t => t.Id).ToListAsync(ct);

    public void Add(ApprovalInstance i) => db.Set<ApprovalInstance>().Add(i);
    public void AddRun(ApprovalStageRun r) => db.Set<ApprovalStageRun>().Add(r);
    public void AddTask(ApprovalTask t) => db.Set<ApprovalTask>().Add(t);
    public void AddAction(ApprovalAction a) => db.Set<ApprovalAction>().Add(a);
    public void AddSnapshotRows(IEnumerable<ApprovalInstanceFlow> rows) => db.Set<ApprovalInstanceFlow>().AddRange(rows);
}
