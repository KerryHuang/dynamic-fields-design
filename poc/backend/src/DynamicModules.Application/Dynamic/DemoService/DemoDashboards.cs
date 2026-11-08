using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 的預設指派版儀表板：登入後首頁就有內容。卡片全部引用 <see cref="DemoReports"/> 的示範報表（真實查詢結果），不寫死任何數字。
/// 業務儀表板給業務主管與業務（同一張，業務只看到自己的訂單所以數字不同）；設備儀表板給保養與現場人員
/// （保養費用卡片現場人員沒有金額權限，顯示無權限，其他卡片正常）；品質儀表板給品保，生產儀表板給生管與現場，進銷存儀表板給進銷存三個群組。MIS（admin）全部都看得到。
/// 名稱是衝突檢查的依據（<see cref="DemoPreflight"/>）；順序就是 Sequence。
/// </summary>
public static class DemoDashboards
{
    public const string Sales = "業務儀表板", Equipment = "設備與保養儀表板", Quality = "品質儀表板", Crm = "CRM 儀表板",
        Production = "生產儀表板", Inventory = "進銷存儀表板";

    /// <param name="ReportName">引用的示範報表名稱（<see cref="DemoReports.Spec.Name"/>）。</param>
    public sealed record CardSpec(string ReportName, string Viz, int X, int Y, int W, int H, CardOptions Options, string? Title = null);

    /// <param name="GroupKeys">指派對象的群組代號（PermissionGroup.Key）；第一個是主要對象。</param>
    public sealed record Spec(string Name, string[] GroupKeys, CardSpec[] Cards);

    private static readonly CardOptions Plain = CardOptions.None;

    private static CardOptions Indicator(string rangeKind, int comparePeriods) =>
        CardOptions.None with { Range = new ReportDateRange(rangeKind, null), ComparePeriods = comparePeriods };

    public static IReadOnlyList<Spec> All { get; } =
    [
        new(Sales, ["sales-manager", "sales", "mis"],
        [
            new("訂單筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), "本月訂單"),
            new("近 90 天訂單狀態分布", "bar", 4, 0, 8, 4, Plain, null),
            new("近 6 個月訂單筆數趨勢", "line", 0, 2, 4, 4, Plain, null),
            new("近 90 天各客戶訂單筆數", "bar", 4, 4, 8, 4,
                CardOptions.None with { TopN = 5, Cumulative = true }, "前 5 大客戶（累計占比）"),
            new("近 90 天各客戶訂單金額", "table", 0, 6, 4, 4,
                CardOptions.None with { TopN = 5, SortKey = "amount", SortDesc = true }, "金額前 5 大客戶"),
        ]),
        new(Equipment, ["maint", "floor", "mis"],
        [
            new("報修筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), "本月報修"),
            new("近 90 天報修狀態", "donut", 4, 0, 4, 4, Plain, null),
            new("近 90 天各類別報修與停機時數", "table", 8, 0, 4, 4, Plain, null),
            new("近 6 個月保養次數趨勢", "line", 0, 2, 4, 4, Plain, null),
            new("近 180 天各模具保養費用", "bar", 4, 4, 8, 4,
                CardOptions.None with { TopN = 6, SortKey = "cost", SortDesc = true }, "保養費用前 6 名的模具"),
        ]),
        new(Quality, ["qa", "mis"],
        [
            new("品質異常筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), "本月品質異常"),
            new("近 60 天品質異常嚴重度", "donut", 4, 0, 4, 4, Plain, null),
            new("近 60 天重大異常（依類型）", "bar", 8, 0, 4, 4, Plain, null),
            new("近 3 個月品質異常筆數趨勢", "line", 0, 2, 4, 4, Plain, null),
        ]),
        // 引用「CRM 與銷售」套件自帶的兩份報表（名稱以套件為準）
        new(Crm, ["sales-manager", "sales", "mis"],
        [
            new("商機階段分布", "donut", 0, 0, 5, 4, Plain, null),
            new("各業務訂單金額（未稅）", "bar", 5, 0, 7, 4, Plain, null),
        ]),
        // 引用「MES 模具廠」「進銷存」套件自帶的報表
        new(Production, ["planner", "floor", "mis"],
        [
            new("工令狀態分布", "donut", 0, 0, 4, 4, Plain, null),
            new("月別完工量", "line", 4, 0, 8, 4, Plain, null),
            new("逾期未完工工令", "table", 0, 4, 6, 4, Plain, null),
            new("月別出貨量", "bar", 6, 4, 6, 4, Plain, null),
        ]),
        new(Inventory, ["ops-manager", "warehouse", "purchasing", "mis"],
        [
            new("庫存餘額（品項 × 倉庫）", "table", 0, 0, 6, 4, Plain, null),
            new("月別進出量", "bar", 6, 0, 6, 4, Plain, null),
            new("採購單狀態分布", "donut", 0, 4, 4, 4, Plain, null),
            new("各供應商採購金額（未稅）", "bar", 4, 4, 8, 4, Plain, null),
        ]),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];
}
