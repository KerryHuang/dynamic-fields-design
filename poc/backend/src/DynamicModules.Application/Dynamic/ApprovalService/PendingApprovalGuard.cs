using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 設計器的定義變更遇到簽核中的實例（設計 §8.6）：會改寫記錄值的定義，和進行中實例要走的轉移與狀態，在有實例 pending 時回 409，
/// 避免簽核人看到的內容在簽核途中被定義變更改掉。判斷只看實例狀態，不看使用者身分；與 ClosedDataGuard、ImmutableDataGuard 掛在同一組檢查點。
/// </summary>
public sealed class PendingApprovalGuard(IApprovalInstanceRepository instances, IApprovalFlowRepository flows, IModuleRepository modules)
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
        foreach (var p in pending)
        {
            if (candidate is null || candidate.StatusFieldId != module.Workflow?.StatusFieldId
                || !StateKept(candidate, active, p.FromOptionId) || !StateKept(candidate, active, p.ToOptionId)
                || !candidate.Transitions.Any(t => t.From == p.FromOptionId && t.To == p.ToOptionId && t.Approval is { Required: true }))
            {
                violating.Add(p);
                continue;
            }
            if (p.FlowVersionId is { } versionId && (await flows.GetVersionAsync(versionId, ct))?.Definition.RejectTo is { } rejectTo
                && (!StateKept(candidate, active, rejectTo) || !candidate.Transitions.Any(t => t.From == p.FromOptionId && t.To == rejectTo)))
                violating.Add(p);
        }
        return violating.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.WorkflowBlockedByPending(Titles(violating), violating.Count));
    }

    /// <summary>停用狀態欄位的選項：進行中實例的 from、to 或綁定版本的退回狀態用到它就回 409。</summary>
    public async Task<ServiceResult?> RejectOptionDeactivateAsync(Module module, long optionId, CancellationToken ct)
    {
        var violating = new List<PendingInstanceRef>();
        foreach (var p in await instances.ListPendingByModulesAsync([module.Id], ct))
            if (p.FromOptionId == optionId || p.ToOptionId == optionId
                || p.FlowVersionId is { } versionId && (await flows.GetVersionAsync(versionId, ct))?.Definition.RejectTo == optionId)
                violating.Add(p);
        return violating.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.WorkflowBlockedByPending(Titles(violating), violating.Count));
    }

    private static bool StateKept(Workflow candidate, HashSet<long> active, long optionId) =>
        candidate.States.Any(s => s.OptionId == optionId) && active.Contains(optionId);

    private static List<string> Titles(IEnumerable<PendingInstanceRef> list) => [.. list.Take(5).Select(p => p.TitleSnapshot)];
}
