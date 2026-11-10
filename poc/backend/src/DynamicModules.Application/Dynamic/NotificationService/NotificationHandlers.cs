using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>先唯讀檢查要不要補提醒或清舊通知，成立才送交易型的 upkeep，再讀清單與未讀數。</summary>
public class GetNotificationsHandler(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, ICurrentUser user, IBusinessTime time, ISender sender)
    : IRequestHandler<GetNotificationsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetNotificationsRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        if (await NeedsUpkeepAsync(userId, ct))
        {
            var upkeep = await sender.Send(new EnsureNotificationUpkeepRequest(), ct);
            if (!upkeep.IsSuccess) return upkeep;
        }
        var take = Math.Clamp(request.Take, 1, NotificationRules.MaxTake);
        var items = (await notifications.ListLatestAsync(userId, take, ct)).Select(NotificationResponse.From).ToList();
        return ServiceResult.Success(new NotificationListResponse(items, await notifications.CountUnreadAsync(userId, ct)));
    }

    private async Task<bool> NeedsUpkeepAsync(string userId, CancellationToken ct)
    {
        foreach (var source in sources)
            if (await source.AnyDueAsync(userId, time.Today, ct)) return true;
        return await notifications.AnyOlderThanAsync(userId, Cutoff(time), ct);
    }

    internal static DateTime Cutoff(IBusinessTime time) => time.Now.AddDays(-NotificationRules.RetentionDays);
}

public class EnsureNotificationUpkeepHandler(
    IEnumerable<INotificationReminderSource> sources, INotificationRepository notifications, ICurrentUser user, IBusinessTime time, IUnitOfWork uow)
    : IRequestHandler<EnsureNotificationUpkeepRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureNotificationUpkeepRequest request, CancellationToken ct)
    {
        var userId = user.UserId;
        foreach (var source in sources) await source.AddDueAsync(userId, time.Today, ct);
        await uow.CommitAsync(ct);
        var cutoff = GetNotificationsHandler.Cutoff(time);
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
