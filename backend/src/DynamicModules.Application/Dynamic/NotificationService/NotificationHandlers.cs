using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>先唯讀檢查要不要補提醒或清舊通知，成立才送交易型的 upkeep，再讀清單與未讀數。</summary>
public class GetNotificationsHandler(
    NotificationUpkeepProbe probe, INotificationRepository notifications, ICurrentUser user, ISender sender, ILogger<GetNotificationsHandler> logger)
    : IRequestHandler<GetNotificationsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetNotificationsRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        // 補提醒與清舊通知是順手做的：失敗（例如某個來源丟例外）只記 log，清單照常回傳
        try
        {
            if (await probe.NeedsUpkeepAsync(userId, ct))
            {
                var upkeep = await sender.Send(new EnsureNotificationUpkeepRequest(), ct);
                if (!upkeep.IsSuccess) logger.LogWarning("補提醒失敗：{Message}", upkeep.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "補提醒失敗");
        }
        var take = Math.Clamp(request.Take, 1, NotificationRules.MaxTake);
        var items = (await notifications.ListLatestAsync(userId, take, ct)).Select(NotificationResponse.From).ToList();
        return ServiceResult.Success(new NotificationListResponse(items, await notifications.CountUnreadAsync(userId, ct)));
    }
}

public class EnsureNotificationUpkeepHandler(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, ICurrentUser user, IBusinessTime time, IUnitOfWork uow,
    NotificationUpkeepProbe probe, IScheduledJobRepository jobs, IAccountRepository accounts)
    : IRequestHandler<EnsureNotificationUpkeepRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureNotificationUpkeepRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        if (request.Fence is { } fence)
        {
            // 排程路徑：租約失效就整筆不寫（別的實例可能已接手）；整批開始後才被停用的帳號跳過
            if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
            if (!await accounts.IsActiveAsync(userId, ct)) return ServiceResult.Success();
        }
        foreach (var source in sources) await source.AddDueAsync(userId, time.Today, ct);
        await uow.CommitAsync(ct);
        var cutoff = await probe.CutoffAsync(ct);
        if (await notifications.AnyOlderThanAsync(userId, cutoff, ct)) await notifications.PurgeOlderThanAsync(userId, cutoff, ct);
        return ServiceResult.Success();
    }
}

public class MarkNotificationReadHandler(INotificationRepository notifications, ICurrentUser user, IBusinessTime time, IUnitOfWork uow)
    : IRequestHandler<MarkNotificationReadRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(MarkNotificationReadRequest request, CancellationToken ct)
    {
        var n = await notifications.GetOwnAsync(request.Id, user.UserId, ct);
        if (n is null) return ServiceResult.NotFound(NotificationMessages.NotFound);
        n.ReadTime ??= time.Now;
        await uow.CommitAsync(ct);
        return ServiceResult.Success(NotificationResponse.From(n));
    }
}

public class MarkAllNotificationsReadHandler(INotificationRepository notifications, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<MarkAllNotificationsReadRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(MarkAllNotificationsReadRequest request, CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(user.UserId, time.Now, ct);
        return ServiceResult.Success();
    }
}
