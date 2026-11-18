namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>維運（監控、稽核、通道）回給使用者的訊息。</summary>
public static class OpsMessages
{
    public const string AuditNotFound = "找不到稽核紀錄";
    public const string AuditRangeReversed = "開始時間不能晚於結束時間";
    public const string AuditCursorInvalid = "分頁位置不正確";
    public const string AuditFilterInvalid = "篩選條件不正確";

    public static string AuditRangeTooLong(int maxDays) => $"查詢區間最長 {maxDays} 天";

    public static string AuditLimitOutOfRange(int max) => $"每頁筆數要在 1 到 {max} 之間";
}
