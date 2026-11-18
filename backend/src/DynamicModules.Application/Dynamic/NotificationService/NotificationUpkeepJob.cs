using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 背景排程的通知維護（維運設計 §10.2.9）：對所有啟用中的使用者做讀通知時那件事——補到期提醒、清掉超過保留天數的通知。
/// 每位使用者一個 DI scope、一個 <see cref="ActingScope"/>、一個交易（主控判斷 B3、O1 計畫判斷 J22），先唯讀判斷，成立才送交易型請求；
/// 一位失敗記 log 後繼續下一位，整體記部分失敗。讀通知時的惰性路徑保留（主控判斷 U4），兩條路徑靠條件式更新去重。
/// </summary>
public sealed class NotificationUpkeepJob(IServiceScopeFactory scopes, ILogger<NotificationUpkeepJob> logger) : IScheduledJob
{
    public string Key => "notification.upkeep";

    public PolicyDefinition Interval => PolicyCatalog.NotificationReminderScanMinutes;

    public async Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var userIds = await services.GetRequiredService<IAccountRepository>().ListActiveUserIdsAsync(ct);
        var failed = 0;
        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var acting = ActingScope.Begin(userId);
                if (!await scope.ServiceProvider.GetRequiredService<NotificationUpkeepProbe>().NeedsUpkeepAsync(userId, ct)) continue;
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EnsureNotificationUpkeepRequest(), ct);
                if (result.IsSuccess) continue;
                failed++;
                logger.LogWarning("替 {UserId} 補提醒失敗：{Message}", userId, result.Message);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(e, "替 {UserId} 補提醒失敗", userId);
            }
        }
        if (failed == 0) return new JobOutcome(ScheduledJobStatus.Succeeded);
        var summary = $"{failed}/{userIds.Count} 位使用者失敗";
        return new JobOutcome(failed == userIds.Count ? ScheduledJobStatus.Failed : ScheduledJobStatus.PartiallyFailed, summary);
    }
}
