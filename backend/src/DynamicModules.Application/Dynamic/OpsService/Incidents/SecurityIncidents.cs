using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 安全類事件（維運設計 §17.2.2、O2-U11）：系統管理員旗標每次異動都通知，同指紋 <c>admin.changed</c> 聚合成一件、累加次數。
/// 「誰給了誰」不存在事件列上：樣本只存那一筆稽核的 Id 與行為者，通知連到那一筆稽核。由 AuditBehavior 在稽核列同一交易呼叫。
/// </summary>
public class SecurityIncidents(IncidentRecorder recorder, IOpsEnvironment env)
{
    public const string AdminChangedCode = "admin.changed";
    /// <summary>有人開始或不再持有設計器任一動作或權限設定（權限軸設計 §9，M9）；寫法照 admin.changed。</summary>
    public const string PrivilegeChangedCode = "privilege.changed";

    /// <remarks>virtual：交易失敗全回滾的測試（PAX-21b）以替身讓它丟例外。</remarks>
    public virtual Task PrivilegeChangedAsync(AuditLogEntry audit, bool granted, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = ErrorFingerprint.ForInfra("security", PrivilegeChangedCode), Source = IncidentSource.Security, Category = IncidentCategory.Security,
        Severity = IncidentSeverity.Warning, Code = PrivilegeChangedCode, TitleKey = IncidentTitles.PrivilegeChanged,
        TitleParams = IncidentTitles.Admin(granted), OccurredUtc = audit.OccurredUtc, Version = env.Version, RequestId = audit.RequestId,
        Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.ActorUserId, null, null, AuditLogId: audit.Id),
        Link = $"/system/audit?id={audit.Id}",
    }, ct);

    public Task AdminChangedAsync(AuditLogEntry audit, bool granted, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = ErrorFingerprint.ForInfra("security", AdminChangedCode), Source = IncidentSource.Security, Category = IncidentCategory.Security,
        Severity = IncidentSeverity.Warning, Code = AdminChangedCode, TitleKey = IncidentTitles.AdminChanged,
        TitleParams = IncidentTitles.Admin(granted), OccurredUtc = audit.OccurredUtc, Version = env.Version, RequestId = audit.RequestId,
        Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.ActorUserId, null, null, AuditLogId: audit.Id),
        Link = $"/system/audit?id={audit.Id}",
    }, ct);
}
