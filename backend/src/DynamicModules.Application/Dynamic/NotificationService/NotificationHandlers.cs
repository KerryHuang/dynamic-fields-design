using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>先唯讀檢查要不要補提醒或清舊通知，成立才送交易型的 upkeep，再讀清單與未讀數。</summary>
public class GetNotificationsHandler(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, ICurrentUser user, IBusinessTime time, ISender sender,
    IPolicyReader policies, ILogger<GetNotificationsHandler> logger)
    : IRequestHandler<GetNotificationsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetNotificationsRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        // 補提醒與清舊通知是順手做的：失敗（例如某個來源丟例外）只記 log，清單照常回傳
        try
        {
            if (await NeedsUpkeepAsync(userId, ct))
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

    private async Task<bool> NeedsUpkeepAsync(string userId, CancellationToken ct)
    {
        foreach (var source in sources)
            if (await source.AnyDueAsync(userId, time.Today, ct)) return true;
        return await notifications.AnyOlderThanAsync(userId, await CutoffAsync(time, policies, ct), ct);
    }

    /// <summary>超過這個時間的通知要清掉；保留天數見政策 notification.retentionDays。</summary>
    internal static async Task<DateTime> CutoffAsync(IBusinessTime time, IPolicyReader policies, CancellationToken ct) =>
        time.Now.AddDays(-await policies.IntAsync(PolicyCatalog.NotificationRetentionDays, null, ct));
}

public class EnsureNotificationUpkeepHandler(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, ICurrentUser user, IBusinessTime time, IUnitOfWork uow,
    IPolicyReader policies)
    : IRequestHandler<EnsureNotificationUpkeepRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureNotificationUpkeepRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        foreach (var source in sources) await source.AddDueAsync(userId, time.Today, ct);
        await uow.CommitAsync(ct);
        var cutoff = await GetNotificationsHandler.CutoffAsync(time, policies, ct);
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
