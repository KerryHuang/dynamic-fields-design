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
    public static IReadOnlySet<long> LockedFields(Module module, Workflow wf, IEnumerable<ApprovalDefinition> definitions) =>
        Lock(module, wf, definitions, _ => true).FieldIds;

    /// <summary>
    /// 鎖定的完整結果：鎖定狀態、鎖定欄位（<see cref="LockedFields"/> 再加本模組所有附件欄位，設計 §9.3「附件在鎖定狀態一律鎖定」）、
    /// 明細列是否一起鎖（閉包含來源是本模組明細的彙總）。isOwnDetailRollup 判斷彙總的來源是不是本模組的明細。
    /// </summary>
    public static ApprovalLock Lock(Module module, Workflow wf, IEnumerable<ApprovalDefinition> definitions, Func<Field, bool> isOwnDetailRollup)
    {
        var defs = definitions.ToList();
        var closure = FieldClosure.Upstream(module, defs.SelectMany(FieldClosure.DirectFields), wf.StatusFieldId, isOwnDetailRollup);
        var ids = closure.FieldIds.ToHashSet();
        ids.UnionWith(defs.SelectMany(d => d.Stages ?? []).SelectMany(s => s.EditableFields ?? []));
        ids.UnionWith(module.Blocks.SelectMany(b => b.Fields).Where(f => f is { Type: FieldType.Attachment, Status: FieldStatus.Active }).Select(f => f.Id));
        ids.Remove(wf.StatusFieldId);
        return new ApprovalLock(LockedStates(wf), ids, closure.IncludesOwnDetailRollup);
    }
}

/// <summary>模組的簽核鎖定結果（設計 §9.3）：鎖定狀態、鎖定欄位（含附件）、明細列是否跟著表頭一起鎖。</summary>
public sealed record ApprovalLock(IReadOnlySet<long> LockedStateIds, IReadOnlySet<long> FieldIds, bool DetailRowsLocked)
{
    public static readonly ApprovalLock None = new(new HashSet<long>(), new HashSet<long>(), false);

    public bool IsLockedState(long? optionId) => optionId is { } id && LockedStateIds.Contains(id);
}
