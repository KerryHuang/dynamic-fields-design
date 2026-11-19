using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 客戶設定類事件（維運設計 §17.2.2 來源表 <c>config</c>）：事件與檢查的已知錯誤碼（警告）、任務規則自動暫停（嚴重）。
/// 指紋＝<c>config</c>＋ruleKind＋moduleKey＋ruleId＋錯誤碼；收件人照 <c>ops.configIssueRecipients</c>，<c>ruleEditor</c> 是規則的最後修改者。
/// 只能在呼叫端的交易裡用。暫停事件在恢復（<c>ruleResumed</c>）或刪規則（<c>ruleDeleted</c>）時結案，不能手動結案、不自動結案。
/// </summary>
public sealed class RuleIncidents(IncidentRecorder recorder, IOpsEnvironment env, TimeProvider clock)
{
    /// <summary>暫停通知與事件連到模組設計頁的任務規則分頁（設計者多半不是系統管理員，看不到監控頁；O2-U3）。</summary>
    public static string RuleLink(string moduleKey, string ruleKey) =>
        $"/system/dynamic-modules/{Uri.EscapeDataString(moduleKey)}?tab=taskRules&rule={Uri.EscapeDataString(ruleKey)}";

    public static string Fingerprint(string ruleKind, string? moduleKey, long ruleId, string code) =>
        ErrorFingerprint.ForConfig(ruleKind, moduleKey, ruleId, code);

    /// <summary>規則的最後修改者，沒有就建立者（§17.2.5）。</summary>
    public static string? EditorOf(AuditableEntity rule) =>
        !string.IsNullOrEmpty(rule.ModifiedUserId) ? rule.ModifiedUserId : string.IsNullOrEmpty(rule.CreatedUserId) ? null : rule.CreatedUserId;

    public Task KnownErrorAsync(RuleFailureNoteEntry note, string? requestId, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = Fingerprint(note.RuleKind, note.ModuleKey, note.RuleId, note.Code), Source = IncidentSource.Config,
        Category = IncidentCategory.Config, Severity = IncidentSeverity.Warning, Code = note.Code,
        TitleKey = IncidentTitles.ConfigPrefix + note.Code, TitleParams = IncidentTitles.Config(note.RuleKind, note.RuleKey, note.ModuleKey, note.Code),
        ModuleKey = note.ModuleKey, RuleKind = note.RuleKind, RuleId = note.RuleId, OccurredUtc = clock.GetUtcNow().UtcDateTime,
        Version = env.Version, RequestId = requestId, RuleEditor = note.RuleEditor,
    }, ct);

    public Task PausedAsync(TaskRule rule, string moduleKey, string? lastErrorCode, int threshold, int failures, DateTime pausedUtc, string? requestId,
        CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = Fingerprint(RuleFailureTag.TaskRule, moduleKey, rule.Id, RuleErrorCodes.AutoPaused), Source = IncidentSource.Config,
        Category = IncidentCategory.Config, Severity = IncidentSeverity.Critical, Code = RuleErrorCodes.AutoPaused, TitleKey = IncidentTitles.RulePaused,
        TitleParams = IncidentTitles.Paused(RuleFailureTag.TaskRule, rule.Key, moduleKey, lastErrorCode, threshold, failures),
        ModuleKey = moduleKey, RuleKind = RuleFailureTag.TaskRule, RuleId = rule.Id, OccurredUtc = pausedUtc, Version = env.Version,
        RequestId = requestId, RuleEditor = EditorOf(rule), NotificationKind = NotificationKinds.OpsRulePaused, Link = RuleLink(moduleKey, rule.Key),
    }, ct);
}
