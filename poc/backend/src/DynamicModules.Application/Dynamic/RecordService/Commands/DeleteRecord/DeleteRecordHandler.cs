using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

public class DeleteRecordHandler(
    IModuleRepository modules, IRecordRepository records, ICurrentUser user, IBusinessTime time,
    IDynamicAccessGuard guard, ReferenceDeletePlanner planner, RollupRecalc rollups, RecordHistoryWriter history)
    : IRequestHandler<DeleteRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteRecordRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return blocked;
        var access = await guard.AuthorizeAsync(module, PermissionAction.Delete, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (record.RowVersion != request.RowVersion) return ServiceResult.Conflict(RecordMapper.Stale);

        if (module.Workflow is { } wf && wf.IsLocked(RecordValueProcessor.Parse(record.ValuesJson)))
            return ServiceResult.Conflict(WorkflowService.WorkflowEnforcer.LockedDeleteMessage);

        var plan = await planner.PlanAsync(record, module, ct);
        if (plan.DepthExceeded) return ServiceResult.Conflict(TooDeep); // 提早停止時 Blocking 不完整，先判深度
        if (plan.Blocking.Count > 0)
            return ServiceResult.ConflictWith(Referenced, new DeleteBlockedResponse(await WithTitlesAsync(plan.Blocking.Take(MaxListed), ct), plan.BlockingTotal));

        // 連帶刪除（Cascade）或清空參照（SetNull）的記錄也要守狀態機的唯讀：在任何寫入之前檢查，有一筆被鎖就整個不做
        if (await TouchesLockedRecordAsync(plan, ct)) return ServiceResult.Conflict(LockedReferencedMessage);

        // 交易由 TransactionBehavior 包住（ITransactionalRequest）：軟刪、清空、索引、彙總重算一起提交或回滾。
        var now = time.Now;
        var deleted = plan.ToDelete.Prepend(record).ToList();
        var deletedIds = deleted.Select(r => r.Id).ToHashSet();
        // 彙總重算（決策 8）：刪除的記錄是 (寫入前, null)，清空的記錄是 (寫入前, 清空後)
        var changes = deleted.Select(r => (r.ModuleId, Before: RecordValueProcessor.Parse(r.ValuesJson), After: (Dictionary<string, JsonElement?>?)null))
            .ToList();
        var cleared = new List<(Record Record, Dictionary<string, JsonElement?> Before, Dictionary<string, JsonElement?> After)>();
        foreach (var (source, field) in plan.ToClear.Where(c => !deletedIds.Contains(c.Record.Id)))
        {
            var values = RecordValueProcessor.Parse(source.ValuesJson);
            var before = new Dictionary<string, JsonElement?>(values);
            values.Remove(field.Key);
            changes.Add((source.ModuleId, before, values));
            cleared.Add((source, before, values));
            source.ValuesJson = RecordValueProcessor.Serialize(values);
            await records.RemoveFieldIndexAsync(source.Id, field.Id, ct);
            Touch(source, now);
        }
        foreach (var r in deleted)
        {
            r.DeleteFlag = true;
            Touch(r, now);
            history.Deleted(r);
        }
        // AfterWriteAsync 負責提交；changes 至少有根記錄，所以一定會呼叫到
        foreach (var group in changes.GroupBy(c => c.ModuleId))
        {
            // 清空的來源記錄要依欄位型別判斷寫哪些歷程，所以載入欄位
            var source = group.Key == module.Id ? module : (await modules.GetByIdAsync(group.Key, includeChildren: true, ct))!;
            foreach (var c in cleared.Where(c => c.Record.ModuleId == group.Key)) history.Updated(c.Record, source, c.Before, c.After);
            await rollups.AfterWriteAsync(source,
                group.Select(c => ((IReadOnlyDictionary<string, JsonElement?>?)c.Before, (IReadOnlyDictionary<string, JsonElement?>?)c.After)),
                deletedIds, ct);
        }
        return ServiceResult.Success();
    }

    /// <summary>連帶刪除或清空的記錄（去重，清空的排除已在刪除清單的）裡，有沒有處於狀態機唯讀狀態的；每個模組只載入一次。</summary>
    private async Task<bool> TouchesLockedRecordAsync(DeletePlan plan, CancellationToken ct)
    {
        var deletedIds = plan.ToDelete.Select(r => r.Id).ToHashSet();
        var affected = plan.ToDelete.Concat(plan.ToClear.Select(c => c.Record).Where(r => !deletedIds.Contains(r.Id))).DistinctBy(r => r.Id);
        var byModule = new Dictionary<long, Module?>();
        foreach (var r in affected)
        {
            if (!byModule.TryGetValue(r.ModuleId, out var m)) byModule[r.ModuleId] = m = await modules.GetByIdAsync(r.ModuleId, includeChildren: true, ct);
            if (m?.Workflow is { } wf && wf.IsLocked(RecordValueProcessor.Parse(r.ValuesJson))) return true;
        }
        return false;
    }

    public const string Referenced = "還有其他資料參照這筆記錄，不能刪除";
    public const string LockedReferencedMessage = "有處於唯讀狀態的單據參照這筆記錄，不能刪除";
    public const string TooDeep = "連帶刪除超過 5 層，請先手動刪除下層資料";
    private const int MaxListed = 10;

    private void Touch(Record r, DateTime now)
    {
        r.RowVersion = Guid.NewGuid().ToString();
        r.StampModified(user.UserId, now);
    }

    /// <summary>使用者有來源模組的 Read、來源記錄在資料範圍內、標題欄位看得到，才帶標題；否則 null。</summary>
    private async Task<List<BlockingRef>> WithTitlesAsync(IEnumerable<BlockingRef> refs, CancellationToken ct)
    {
        var result = new List<BlockingRef>();
        foreach (var group in refs.GroupBy(r => r.ModuleKey))
        {
            var source = await modules.GetByKeyAsync(group.Key, includeChildren: true, ct);
            var perms = source is null ? null : (await guard.AuthorizeAsync(source, PermissionAction.Read, ct)).Permissions;
            var readable = perms is not null && source!.TitleVisibleTo(perms);
            foreach (var b in group)
            {
                var rec = readable ? await records.GetAsync(b.RecordId, ct) : null;
                var visible = rec is not null && (perms!.OwnerUserIds is null || perms.OwnerUserIds.Contains(rec.OwnerUserId));
                result.Add(b with { Title = visible ? rec!.Title : null });
            }
        }
        return result;
    }
}
