using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 一則通知。維運兩個種類（<c>opsIncident</c>、<c>opsRulePaused</c>）另帶標題代碼與白名單參數，鈴鐺依語系組字（維運設計 §17.2.5）；
/// <see cref="Title"/> 是寫入時的繁中快照，只當前端沒有組字程式時的退路。其他種類的 <c>DataJson</c> 不輸出。
/// </summary>
public sealed record NotificationResponse(long Id, string Kind, string Title, string Link, bool Read, DateTime CreatedTime,
    string? TitleKey = null, IReadOnlyDictionary<string, object>? TitleParams = null)
{
    public static NotificationResponse From(Notification n)
    {
        var (key, p) = n.Kind is NotificationKinds.OpsIncident or NotificationKinds.OpsRulePaused ? OpsTitle(n.DataJson) : (null, null);
        return new(n.Id, n.Kind, n.TitleSnapshot, n.Link, n.ReadTime is not null, n.CreatedTime, key, p);
    }

    /// <summary>讀 <c>{ titleKey, params }</c>：數字回 long、其他回字串；格式不對就當沒有（退回快照）。</summary>
    private static (string?, IReadOnlyDictionary<string, object>?) OpsTitle(string? json)
    {
        if (string.IsNullOrEmpty(json)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("titleKey", out var k) || k.ValueKind != JsonValueKind.String)
                return (null, null);
            var p = new Dictionary<string, object>(StringComparer.Ordinal);
            if (root.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var e in ps.EnumerateObject())
                    p[e.Name] = e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetInt64(out var l) ? l : e.Value.ToString();
            return (k.GetString(), p);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int UnreadCount);

public static class NotificationMessages
{
    public const string NotFound = "找不到通知";
}

public static class NotificationRules
{
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
