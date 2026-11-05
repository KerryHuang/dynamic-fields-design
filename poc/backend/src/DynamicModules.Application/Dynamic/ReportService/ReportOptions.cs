namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>報表設定；註冊成 singleton，WebAPI 從 Reports:MaxRows 讀取，測試可調低上限。</summary>
public sealed class ReportOptions
{
    public int MaxRows { get; set; } = 50_000;
}
