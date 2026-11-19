namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>事件與檢查回傳的已知規則錯誤碼（維運設計 §17.2.2 O2-T17）。</summary>
public static class RuleErrorCodes
{
    public const string EventTooDeep = "event.tooDeep", EventLoop = "event.loop", CheckConditionInvalid = "check.conditionInvalid",
        EffectTargetUnavailable = "effect.targetUnavailable", EffectMissingDetail = "effect.missingDetail", EffectTooManyRows = "effect.tooManyRows",
        EffectHandlerMissing = "effect.handlerMissing", EffectUnknownKind = "effect.unknownKind";

    /// <summary>任務規則自動暫停的事件代碼（指紋與標題用；§17.2.2 來源表）。</summary>
    public const string AutoPaused = "autoPaused";

    public static readonly IReadOnlyList<string> All =
        [EventTooDeep, EventLoop, CheckConditionInvalid, EffectTargetUnavailable, EffectMissingDetail, EffectTooManyRows, EffectHandlerMissing, EffectUnknownKind];
}

/// <summary>一筆已知錯誤碼：規則種類、id、代碼，以及開客戶設定事件要用的模組代碼、規則代碼、最後修改者。</summary>
public sealed record RuleFailureNoteEntry(string RuleKind, long RuleId, string Code, string? ModuleKey, string? RuleKey, string? RuleEditor);

/// <summary>
/// 這個請求裡事件與檢查回傳的已知錯誤碼（scoped；維運設計 §17.2.2、O2-T17）。請求結果是失敗時，<c>RuleFailureBehavior</c> 在交易外
/// 開或更新客戶設定事件，不計數、不暫停。巢狀請求共用同一個 scope，所以內層記的也由最外層處理。
/// </summary>
public sealed class RuleFailureNote
{
    private readonly List<RuleFailureNoteEntry> _entries = [];

    public IReadOnlyList<RuleFailureNoteEntry> Entries => _entries;

    public void Add(string ruleKind, long ruleId, string code, string? moduleKey = null, string? ruleKey = null, string? ruleEditor = null)
    {
        if (!_entries.Any(e => e.RuleKind == ruleKind && e.RuleId == ruleId && e.Code == code))
            _entries.Add(new RuleFailureNoteEntry(ruleKind, ruleId, code, moduleKey, ruleKey, ruleEditor));
    }

    public void Clear() => _entries.Clear();
}
