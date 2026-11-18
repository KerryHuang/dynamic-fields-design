namespace DynamicModules.Domain.Tasks;

/// <summary>父任務的子任務數：Total 含已完成與已取消；進度＝Done ÷（Total − Cancelled），由前端組字（看板設計 §10.5、ST-T10）。</summary>
public sealed record TaskProgress(int Done, int Cancelled, int Total);

/// <summary>
/// 任務層級（看板設計 §10.7.4 第 9 項；DTO 的 level 與報表 -16 同一個定義）：有父任務是子任務；沒有父任務但底下至少一個子任務（任何狀態）是父任務；
/// 其餘是獨立任務。是資料屬性，不隨觀看者變。
/// </summary>
public static class TaskLevel
{
    public const string Standalone = "standalone", Parent = "parent", Child = "child";

    public static string Of(long? parentTaskId, bool hasChildren) =>
        parentTaskId is not null ? Child : hasChildren ? Parent : Standalone;
}
