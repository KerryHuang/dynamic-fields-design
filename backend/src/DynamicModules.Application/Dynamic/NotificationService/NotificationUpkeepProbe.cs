using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 唯讀判斷這位使用者要不要補提醒或清舊通知；讀通知清單與背景排程（<c>notification.upkeep</c>）共用，
/// 成立才送交易型的 <see cref="EnsureNotificationUpkeepRequest"/>，免得每次都搶寫入鎖。
/// </summary>
public sealed class NotificationUpkeepProbe(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, IBusinessTime time, IPolicyReader policies)
{
    public async Task<bool> NeedsUpkeepAsync(string userId, CancellationToken ct)
    {
        foreach (var source in sources)
            if (await source.AnyDueAsync(userId, time.Today, ct)) return true;
        return await notifications.AnyOlderThanAsync(userId, await CutoffAsync(ct), ct);
    }

    /// <summary>超過這個時間的通知要清掉；保留天數見政策 notification.retentionDays。</summary>
    public async Task<DateTime> CutoffAsync(CancellationToken ct) =>
        time.Now.AddDays(-await policies.IntAsync(PolicyCatalog.NotificationRetentionDays, null, ct));
}
