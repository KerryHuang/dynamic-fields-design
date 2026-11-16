namespace DynamicModules.Application.Dynamic.TaskViewService;

public static class TaskViewMessages
{
    public const string NotFound = "這個看板已刪除或不再分享給你";
    public const string NameRequired = "看板名稱必填";
    public static string NameTooLong(int max) => $"看板名稱最多 {max} 字";
    public const string KindInvalid = "目前只支援看板";
    public static string TooManyShares(int max) => $"每個看板最多分享給 {max} 個對象";
    public const string ShareKindInvalid = "分享對象的種類必須是使用者、群組或部門";
    public const string ShareTargetMissing = "分享對象不存在";
    public const string CopySuffix = "（副本）";
    public const string SourceBoth = "只能帶看板 Id 或看板設定其中一個";
}
