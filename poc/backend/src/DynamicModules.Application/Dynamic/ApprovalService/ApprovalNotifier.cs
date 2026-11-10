using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>簽核的站內通知（設計 §12）：同一個交易內寫入，只帶標題快照與連結。</summary>
public sealed class ApprovalNotifier(INotificationRepository notifications, IBusinessTime time, IModuleRepository modules)
{
    public const string SourceType = "approval";

    /// <summary>去重；exceptUserId（這次動作的人）不通知。</summary>
    public async Task NotifyAsync(string kind, ApprovalInstance inst, IEnumerable<string> userIds, string? exceptUserId, CancellationToken ct)
    {
        var recipients = userIds.Where(u => u != exceptUserId).Distinct(StringComparer.Ordinal).ToList();
        if (recipients.Count == 0) return;
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: false, ct);
        foreach (var userId in recipients)
            notifications.Add(new Notification
            {
                UserId = userId, Kind = kind, TitleSnapshot = inst.TitleSnapshot, Link = $"/dynamic/{module?.Key}/{inst.RecordId}",
                SourceType = SourceType, SourceId = inst.Id, CreatedTime = time.Now
            });
    }
}
