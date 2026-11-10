using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Approvals;

/// <summary>狀態機上的可編輯狀態與簽核鎖定狀態（設計 §9.3）；純函式，只看狀態機定義。</summary>
public static class ApprovalTopology
{
    /// <summary>起始狀態，加上設計者標記 Editable 的狀態。</summary>
    public static IReadOnlySet<long> EditableStates(Workflow wf)
    {
        var set = wf.States.Where(s => s.Editable).Select(s => s.OptionId).ToHashSet();
        set.Add(wf.InitialOptionId);
        return set;
    }

    /// <summary>
    /// 起點：需簽核轉移的目標狀態（即使被標成可編輯也算，以鎖定為準）。
    /// 延伸：從鎖定狀態沿不需簽核的轉移走到的狀態，遇到可編輯狀態就停（可編輯狀態本身不算）。
    /// </summary>
    public static IReadOnlySet<long> LockedStates(Workflow wf)
    {
        var editable = EditableStates(wf);
        var locked = wf.Transitions.Where(t => t.Approval is { Required: true }).Select(t => t.To).ToHashSet();
        var pending = new Stack<long>(locked);
        while (pending.TryPop(out var state))
            foreach (var t in wf.Transitions.Where(t => t.From == state && t.Approval is not { Required: true }))
                if (!editable.Contains(t.To) && locked.Add(t.To)) pending.Push(t.To);
        return locked;
    }

    public static bool IsLocked(Workflow wf, long? optionId) => optionId is { } id && LockedStates(wf).Contains(id);

    /// <summary>需簽核轉移的目標狀態。</summary>
    public static IReadOnlySet<long> ApprovalTargets(Workflow wf) =>
        wf.Transitions.Where(t => t.Approval is { Required: true }).Select(t => t.To).ToHashSet();

    /// <summary>
    /// 鎖定欄位（設計 §9.3）：definitions 是掛在需簽核目標狀態上的所有流程、各版本。
    /// 條件與 userField 引用的欄位經公式、彙總、帶值的上游閉包，加上所有可編輯欄位；狀態欄位除外。
    /// </summary>
    public static IReadOnlySet<long> LockedFields(Module module, Workflow wf, IEnumerable<ApprovalDefinition> definitions)
    {
        var defs = definitions.ToList();
        var ids = FieldClosure.Upstream(module, defs.SelectMany(FieldClosure.DirectFields), wf.StatusFieldId).FieldIds.ToHashSet();
        ids.UnionWith(defs.SelectMany(d => d.Stages ?? []).SelectMany(s => s.EditableFields ?? []));
        ids.Remove(wf.StatusFieldId);
        return ids;
    }
}
