namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>報表設定；註冊成 singleton，WebAPI 從 Reports:MaxRows、Reports:MaxLoadRows 讀取，測試可調低上限。</summary>
public sealed class ReportOptions
{
    /// <summary>合併且套用條件後的列數上限（spec §5）。</summary>
    public int MaxRows { get; set; } = 50_000;

    /// <summary>單一來源模組載入記錄的上限，只防記憶體；條件要載入後才能套用，所以縮小條件也過不了這關。</summary>
    public int MaxLoadRows { get; set; } = 200_000;
}
