using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 維運事件的站內通知（維運設計 §17.2.5）：和事件轉換寫在同一個交易。只帶標題代碼＋白名單參數（<c>DataJson</c>）與繁中快照，
/// 不放正規化訊息與樣本。收件人：客戶設定類照 <c>ops.configIssueRecipients</c>（<c>ruleEditor</c> 解析成規則的最後修改者），其他類照
/// <c>ops.alertRecipients</c>；解析為空時改送全部啟用中的系統管理員（寫死的安全網，比照簽核異常通知）。
/// </summary>
public sealed class OpsNotifier(
    INotificationRepository notifications, IPermissionRepository permissions, IPolicyReader policies, RecipientResolver recipients,
    IBusinessTime time, LocaleRegistry registry, ILogger<OpsNotifier> logger)
{
    public const string SourceType = "opsIncident";

    public static string IncidentLink(long incidentId) => $"/system/ops?tab=incidents&incident={incidentId}";

    public async Task NotifyAsync(long incidentId, string titleKey, IReadOnlyDictionary<string, object>? titleParams, string? link, CancellationToken ct,
        IncidentCategory category = IncidentCategory.Defect, string? ruleEditor = null, string? kind = null)
    {
        var policy = category == IncidentCategory.Config ? PolicyCatalog.OpsConfigIssueRecipients : PolicyCatalog.OpsAlertRecipients;
        var to = await recipients.ResolveAsync(await policies.ListAsync(policy, null, ct), ct, ruleEditor);
        if (to.Count == 0)
        {
            logger.LogWarning("維運事件通知對象政策 {Policy} 解析為空，改通知全部系統管理員（事件 {IncidentId}）", policy.Code, incidentId);
            to = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId).ToList();
        }
        var data = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["titleKey"] = titleKey, ["params"] = titleParams ?? new Dictionary<string, object>(), ["incidentId"] = incidentId,
        });
        // 寫進資料的系統文字用系統預設語系（N1、N2）
        var def = await policies.ChoiceAsync(PolicyCatalog.LocaleDefault, null, ct);
        var snapshot = IncidentTitles.Snapshot(registry.ContextFor(def, def), titleKey, titleParams);
        foreach (var userId in to.Distinct(StringComparer.Ordinal))
            notifications.Add(new Notification
            {
                UserId = userId, Kind = kind ?? NotificationKinds.OpsIncident, TitleSnapshot = snapshot, Link = link ?? IncidentLink(incidentId),
                SourceType = SourceType, SourceId = incidentId, DataJson = data, CreatedTime = time.Now,
            });
    }
}
