namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 儀表板。OwnerUserId 為 null 是指派版（只有系統管理員能建立與修改，Version 每次更新加一）；有值是那位使用者自己的儀表板。
/// 版面存成 LayoutJson（格式見 DashboardLayout）。群組無 FK，刪除群組時由 DeleteGroupAsync 明確清除指派。
/// </summary>
public class Dashboard : AuditableEntity
{
    public long Id { get; set; }
    public string? OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string LayoutJson { get; set; } = "{\"cards\":[]}";
    public int Version { get; set; } = 1;
    /// <summary>指派版的顯示順序，小的在前；自己的儀表板不用。</summary>
    public int Sequence { get; set; }
    public List<DashboardAssignment> Assignments { get; set; } = [];
}
