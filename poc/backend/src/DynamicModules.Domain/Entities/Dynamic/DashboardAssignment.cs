namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>指派版儀表板指派給某個群組；群組無 FK。</summary>
public class DashboardAssignment
{
    public long DashboardId { get; set; }
    public long GroupId { get; set; }
}
