namespace DynamicModules.Application.Dynamic.ExportService;

/// <summary>匯出設定；註冊成 singleton，測試可調低上限而不必建 10001 筆資料。</summary>
public sealed class ExportOptions
{
    public int MaxRecords { get; set; } = 10000;
}
