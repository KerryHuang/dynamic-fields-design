using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 改公式、彙總、帶值或重新啟用欄位後，重檢「可編輯欄位不落在流程條件上游閉包」（設計 §9.2）。
/// 只報這次改動新增的違規（和改動前相比），既有的違規不連坐；模組沒有需簽核轉移時一律略過、不查資料庫。
/// </summary>
public sealed class ApprovalClosureRecheck(IApprovalFlowRepository flows)
{
    /// <summary>
    /// 暫時把 field 換成 candidate 設定與狀態，重算閉包交集，和換之前的錯誤相比，有新增就回 400（鍵為 flows[流程鍵]）。
    /// 比對放在整批重算之前，擋下時不寫任何記錄；換上去的設定在回傳前一定還原。
    /// </summary>
    public async Task<ServiceResult?> RejectNewAsync(Module module, Field field, FieldConfig candidate, FieldStatus status, CancellationToken ct)
    {
        if (!HasApproval(module)) return null;
        var current = await flows.ListCurrentByModuleAsync(module.Id, ct);
        if (current.Count == 0) return null;
        var seen = ApprovalConstraints.ClosureViolations(module, module.Workflow!, current).Select(v => (v.FlowKey, v.FieldId)).ToHashSet();
        var (config, oldStatus) = (field.Config, field.Status);
        IReadOnlyList<ApprovalConstraints.ClosureViolation> after;
        field.Config = candidate;
        field.Status = status;
        try { after = ApprovalConstraints.ClosureViolations(module, module.Workflow!, current); }
        finally { (field.Config, field.Status) = (config, oldStatus); }

        // 以（流程鍵、可編輯欄位）識別違規，路徑改變不算新增
        var added = after.Where(v => !seen.Contains((v.FlowKey, v.FieldId)))
            .GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Select(v => v.Message).Distinct().ToArray());
        return added.Count == 0 ? null : ServiceResult.Invalid(added);
    }

    /// <summary>鎖定帶值（lock）的目的欄位是目前版本任何一關的可編輯欄位：鎖定帶值與簽核人填寫互相衝突，回 400（鍵 FieldRules.FillKey）。進行中實例綁舊版時改帶值已由 PendingApprovalGuard 擋下，舊版與已完成的實例不再需要簽核人填寫。</summary>
    public async Task<ServiceResult?> RejectLockFillIntoEditableAsync(Module module, FieldConfig candidate, CancellationToken ct)
    {
        if (candidate.Fill is not { Mode: FillConfig.Lock } fill || fill.Map.Count == 0 || !HasApproval(module)) return null;
        var editable = (await flows.ListCurrentByModuleAsync(module.Id, ct))
            .SelectMany(f => f.Definition.Stages ?? []).SelectMany(s => s.EditableFields ?? []).ToHashSet();
        if (fill.Map.FirstOrDefault(m => editable.Contains(m.To)) is not { } hit) return null;
        var label = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == hit.To)?.LabelI18n.Resolve(I18nText.FallbackLocale) ?? hit.To.ToString();
        return ServiceResult.Invalid(new Dictionary<string, string[]> { [FieldRules.FillKey] = [ApprovalMessages.FillTargetEditable(label)] });
    }

    private static bool HasApproval(Module module) => module.Workflow is { } wf && wf.Transitions.Any(t => t.Approval is { Required: true });
}
