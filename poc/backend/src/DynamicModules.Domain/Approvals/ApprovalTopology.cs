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
}
