using System.Text.Json;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 模組的狀態機（一模組最多一個）：掛在一個單選下拉欄位上，定義起始狀態、狀態（可標唯讀）與允許的轉移。
/// 狀態一律以選項 Id 表示，記錄裡存的是 opt_&lt;id&gt;。
/// </summary>
public class Workflow : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public long StatusFieldId { get; set; }
    public long InitialOptionId { get; set; }
    public List<WorkflowState> States { get; set; } = [];
    public List<WorkflowTransition> Transitions { get; set; } = [];

    public string StatusFieldKey => $"f_{StatusFieldId}";

    /// <summary>記錄目前的狀態選項 Id；沒有值或不是 opt_&lt;id&gt; 回 null。</summary>
    public long? StateOf(IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (!values.TryGetValue(StatusFieldKey, out var v) || v is not { ValueKind: JsonValueKind.String } s) return null;
        var text = s.GetString();
        return text is not null && text.StartsWith("opt_", StringComparison.Ordinal) && long.TryParse(text.AsSpan(4), out var id) ? id : null;
    }

    public bool IsReadOnlyState(long? optionId) => optionId is { } id && States.Any(s => s.OptionId == id && s.ReadOnly);

    /// <summary>記錄目前是否處於唯讀狀態（除了狀態欄位以外都不能改）。</summary>
    public bool IsLocked(IReadOnlyDictionary<string, JsonElement?> values) => IsReadOnlyState(StateOf(values));

    public WorkflowTransition? Find(long from, long to) => Transitions.FirstOrDefault(t => t.From == from && t.To == to);

    /// <summary>From → To 是已定義且需簽核的轉移。</summary>
    public bool RequiresApproval(long from, long to) => Find(from, to)?.Approval is { Required: true };

    /// <summary>以 to 為目標狀態的所有轉移（入口）。</summary>
    public IEnumerable<WorkflowTransition> EntriesOf(long to) => Transitions.Where(t => t.To == to);
}

/// <summary>
/// 參與狀態機的選項；ReadOnly 表示處於這個狀態的記錄除了狀態欄位以外都不能改、不能刪。
/// Editable 是設計者標記的可編輯狀態（退回重送），簽核鎖定的延伸到這裡為止（簽核設計 §9.3）。
/// </summary>
public record WorkflowState(long OptionId, bool ReadOnly = false, bool Editable = false);

/// <summary>From → To 的轉移；GroupIds 是能推進的群組，空陣列代表有該模組 Edit 權限的人都可以。Approval 不為 null 時是需簽核的設定。</summary>
public record WorkflowTransition(long From, long To, List<long> GroupIds, TransitionApproval? Approval = null);

/// <summary>轉移的簽核設定；NoMatch 是沒有流程符合時的處理：block（擋下）或 direct（直接轉移）。</summary>
public record TransitionApproval(bool Required, string NoMatch = "block");
