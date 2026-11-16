namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 使用者對指派版儀表板的個人調整（第一次排版或卡片異動時另存）。BaseVersion 是建立當下指派版的版本號，
/// 比指派版目前的 Version 小就表示管理員之後更新過。取消指派時這筆保留，重新指派時恢復。
/// </summary>
public class DashboardPersonalVersion
{
    public long DashboardId { get; set; }
    public string UserId { get; set; } = "";
    public string LayoutJson { get; set; } = "{\"cards\":[]}";
    public int BaseVersion { get; set; }
}
