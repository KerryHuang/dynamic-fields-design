using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

public sealed record NotificationResponse(long Id, string Kind, string Title, string Link, bool Read, DateTime CreatedTime)
{
    public static NotificationResponse From(Notification n) => new(n.Id, n.Kind, n.TitleSnapshot, n.Link, n.ReadTime is not null, n.CreatedTime);
}

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int UnreadCount);

public static class NotificationMessages
{
    public const string NotFound = "找不到通知";
}

public static class NotificationRules
{
    public const int RetentionDays = 90;
    public const int MaxTake = 100;
}

/// <summary>
/// 讀通知時補提醒的來源（核心沒有背景服務，設計 §6.2）。每種來源一個實作，各自記「今天已提醒」，同一種提醒一天最多一則。
/// </summary>
public interface INotificationReminderSource
{
    /// <summary>唯讀：這個人今天有沒有該補的提醒。</summary>
    Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct);

    /// <summary>交易內：再判斷一次，補寫提醒並記下已提醒；不提交。</summary>
    Task AddDueAsync(string userId, DateOnly today, CancellationToken ct);
}
