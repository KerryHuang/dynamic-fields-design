using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 設計器的定義變更遇到簽核中的實例（設計 §8.6）：會改寫記錄值的定義，和進行中實例要走的轉移與狀態，在有實例 pending 時回 409，
/// 避免簽核人看到的內容在簽核途中被定義變更改掉。判斷只看實例狀態，不看使用者身分；與 ClosedDataGuard、ImmutableDataGuard 掛在同一組檢查點。
/// </summary>
public sealed class PendingApprovalGuard(IApprovalInstanceRepository instances, IApprovalFlowRepository flows, IModuleRepository modules, IRecordRepository records)
{
    /// <summary>
    /// 改寫值的範圍：本模組、以它為來源的彙總所在的父模組（公式重算會連帶重算），以及本模組是明細時的表頭模組。
    /// 有實例 pending 就回 409，否則 null。
    /// </summary>
    public async Task<ServiceResult?> RejectValueRewriteAsync(Module module, CancellationToken ct)
    {
        var ids = new HashSet<long> { module.Id };
        foreach (var (parent, _, _) in await modules.ListRollupsBySourceModuleAsync(module.Id, ct)) ids.Add(parent.Id);
        if (ModuleRelations.ParentKeyOf(module) is { } parentKey
            && await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is { } header)
            ids.Add(header.Id);
        var pending = await instances.ListPendingByModulesAsync(ids, ct);
        return pending.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.DefinitionBlockedByPending(Titles(pending), pending.Count));
    }

    /// <summary>
    /// 刪除欄位或區塊：清除會改寫記錄值時（有已軟刪除記錄、索引列或歷程列要清，或公式、彙總欄位在未刪除記錄上有值）才擋；
    /// 公式與彙總沒有歷程也沒有索引列，Purge 全為 0 不代表沒有值要清。什麼都不清的刪除（空欄位、空區塊）照常放行。
    /// </summary>
    public async Task<ServiceResult?> RejectFieldPurgeAsync(Module module, PurgeCounts purge, IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        if (purge is { Records: 0, IndexRows: 0, HistoryRows: 0 } && !await ComputedValueProbe.HasValuesAsync(records, module, fields, ct)) return null;
        return await RejectValueRewriteAsync(module, ct);
    }

    /// <summary>
    /// 存狀態機（candidate）或刪除狀態機（candidate 為 null）。進行中實例綁定的 from、to 狀態必須仍在且啟用、狀態欄位沒換、
    /// from → to 仍存在且仍需簽核；綁定版本有退回狀態時，from → 退回狀態的轉移也要還在。
    /// </summary>
    public async Task<ServiceResult?> RejectWorkflowChangeAsync(Module module, Workflow? candidate, CancellationToken ct)
    {
        var pending = await instances.ListPendingByModulesAsync([module.Id], ct);
        if (pending.Count == 0) return null;
        var violating = new List<PendingInstanceRef>();
        var active = module.Workflow is { } current
            ? module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == current.StatusFieldId)?.Options.Where(o => o.Active).Select(o => o.Id).ToHashSet() ?? []
            : [];
        var rejectTos = await RejectTosAsync(pending, ct);
        foreach (var p in pending)
        {
            if (candidate is null || candidate.StatusFieldId != module.Workflow?.StatusFieldId
                || !StateKept(candidate, active, p.FromOptionId) || !StateKept(candidate, active, p.ToOptionId)
                || !candidate.Transitions.Any(t => t.From == p.FromOptionId && t.To == p.ToOptionId && t.Approval is { Required: true }))
            {
                violating.Add(p);
                continue;
            }
            // rejectTo 等於 from 是「退回留在原地」，不需要轉移，與執行期（ApprovalActionHandlers）的寫法一致
            if (RejectToOf(rejectTos, p) is { } rejectTo && rejectTo != p.FromOptionId
                && (!StateKept(candidate, active, rejectTo) || !candidate.Transitions.Any(t => t.From == p.FromOptionId && t.To == rejectTo)))
                violating.Add(p);
        }
        return violating.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.WorkflowBlockedByPending(Titles(violating), violating.Count));
    }

    /// <summary>停用狀態欄位的選項：進行中實例的 from、to 或綁定版本的退回狀態用到它就回 409。</summary>
    public async Task<ServiceResult?> RejectOptionDeactivateAsync(Module module, long optionId, CancellationToken ct)
    {
        var pending = await instances.ListPendingByModulesAsync([module.Id], ct);
        var rejectTos = await RejectTosAsync(pending, ct);
        var violating = pending.Where(p => p.FromOptionId == optionId || p.ToOptionId == optionId || RejectToOf(rejectTos, p) == optionId).ToList();
        return violating.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.WorkflowBlockedByPending(Titles(violating), violating.Count));
    }

    /// <summary>進行中實例綁定的各版本的退回狀態（一次查完，不追蹤）。</summary>
    private async Task<Dictionary<long, long?>> RejectTosAsync(IEnumerable<PendingInstanceRef> pending, CancellationToken ct) =>
        (await flows.ListVersionsAsync([.. pending.Select(p => p.FlowVersionId).OfType<long>().Distinct()], ct))
        .ToDictionary(v => v.Id, v => v.Definition.RejectTo);

    private static long? RejectToOf(Dictionary<long, long?> rejectTos, PendingInstanceRef p) =>
        p.FlowVersionId is { } id && rejectTos.TryGetValue(id, out var rt) ? rt : null;

    private static bool StateKept(Workflow candidate, HashSet<long> active, long optionId) =>
        candidate.States.Any(s => s.OptionId == optionId) && active.Contains(optionId);

    private static List<string> Titles(IEnumerable<PendingInstanceRef> list) => [.. list.OrderBy(p => p.InstanceId).Take(5).Select(p => p.TitleSnapshot)];
}
