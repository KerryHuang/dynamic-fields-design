namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>報表分享給某個群組；群組無 FK，刪除群組時由 DeleteGroupAsync 明確清除。</summary>
public class ReportShare
{
    public long ReportId { get; set; }
    public long GroupId { get; set; }
}
