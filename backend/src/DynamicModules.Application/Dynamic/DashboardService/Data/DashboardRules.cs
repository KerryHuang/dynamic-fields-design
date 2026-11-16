using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.DashboardService.Data;

/// <summary>儀表板版面的存檔驗證（結構，不碰資料庫）。報表可見性由 <see cref="DashboardAccess"/> 檢查。</summary>
public static class DashboardRules
{
    public const string NotFound = "儀表板不存在", BadVersion = "版本不正確，請重新載入後再改";
    public const int Columns = 12, MaxCards = 40, MaxHeight = 40, MaxTitleLength = 100;

    private static readonly string[] Vizs = ["table", "indicator", "line", "bar", "donut"];

    public static List<string> CheckLayout(DashboardLayout layout)
    {
        var errors = new List<string>();
        if (layout.Cards.Count > MaxCards) errors.Add($"卡片最多 {MaxCards} 張");
        if (layout.Cards.Any(c => c is null))
        {
            errors.Add("卡片不可為空");
            return errors;
        }
        if (layout.Cards.Select(c => c.Id).Distinct().Count() != layout.Cards.Count || layout.Cards.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 40))
            errors.Add("卡片 Id 不可空白、重複或超過 40 字");
        foreach (var c in layout.Cards)
        {
            if (!Vizs.Contains(c.Viz)) errors.Add("圖表種類不合法");
            if (c.X < 0 || c.Y < 0 || c.W < 1 || c.H < 1 || c.X + c.W > Columns || c.H > MaxHeight) errors.Add("卡片位置或大小不合法");
            if (c.Title is { Length: > MaxTitleLength }) errors.Add($"卡片標題最多 {MaxTitleLength} 個字");
            CheckOptions(c, errors);
        }
        return errors.Distinct().ToList();
    }

    private static void CheckOptions(DashboardCard c, List<string> errors)
    {
        var o = c.Options;
        if (ReportRules.CheckRange(o.Range) is { } rangeError) errors.Add(rangeError);
        if (o.TopN is { } n && (n < 1 || n > 100)) errors.Add("前 N 名的 N 必須介於 1 到 100");
        if (o.ComparePeriods is { } p && (p < 1 || p > 12)) errors.Add("期間比較的期數必須介於 1 到 12");
        if (o.Cumulative && c.Viz != "bar") errors.Add("累計占比只限長條圖");
        if (o.ComparePeriods is not null && c.Viz != "indicator") errors.Add("期間比較只限指標卡");
        if (o.SortKey is { Length: > 60 } || o.SortKey?.Length == 0) errors.Add("排序鍵不合法");
        if (o.LinkDashboardId is <= 0) errors.Add("連結的儀表板不合法");
    }
}
