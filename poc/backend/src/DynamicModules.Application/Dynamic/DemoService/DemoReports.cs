using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 的示範報表定義：每個情境至少兩張，分享給該情境的主管群組；訂單狀態分布另分享給業務，示範同一張報表依權限數字不同；各客戶訂單金額另分享給現場人員，示範沒有金額權限時整張無權限。名稱是衝突檢查的依據（<see cref="DemoPreflight"/>）。
/// module 參數與欄位引用用範本的模組代號（不是 Module.Key）。日期區間的天數涵蓋各情境產生的資料跨度（訂單 79 天、報修 90 天、保養 180 天、品質異常 60 天）。
/// </summary>
public static class DemoReports
{
    /// <param name="GroupKeys">分享對象的群組代號（PermissionGroup.Key）。</param>
    public sealed record Spec(string Name, string[] GroupKeys, Func<TemplateIdMap, ReportDefinition> Build);

    public static IReadOnlyList<Spec> All { get; } =
    [
        new("近 90 天訂單狀態分布", ["sales-manager", "sales"], ids => Def(ids, "order", "bar",
            groups: [Group(Main(ids, "order.status"))],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: Last(90))),
        // 用到金額，現場人員沒有金額權限，開啟時看到無權限。合併訂單明細，依客戶（帶出參照欄位的標題）分組；明細欄位供下鑽
        new("近 90 天各客戶訂單金額", ["sales-manager", "floor"], ids => Def(ids, "order", "table",
            child: new ReportChild(ids.ModuleKeys["line"], ids.FieldIds["line.order"]),
            columns: [Child(ids, "line.itemName"), Child(ids, "line.qty")],
            groups: [Group(new ReportFieldRef("main", 0, ids.FieldIds["order.customer"]))],
            measures: [Count(), Sum("amount", Child(ids, "line.amount"))],
            primary: Main(ids, "order.orderDate"), range: Last(90))),

        new("近 90 天報修狀態", ["maint"], ids => Def(ids, "repair", "donut",
            groups: [Group(Main(ids, "repair.status"))],
            measures: [Count()], primary: Main(ids, "repair.reportDate"), range: Last(90))),
        new("近 90 天各類別報修與停機時數", ["maint"], ids => Def(ids, "repair", "table",
            groups: [Group(Main(ids, "repair.category"))],
            measures: [Count(), Sum("downtime", Main(ids, "repair.downtime"))],
            primary: Main(ids, "repair.reportDate"), range: Last(90))),

        new("近 180 天各模具保養費用", ["maint"], ids => Def(ids, "care", "bar",
            groups: [Group(Main(ids, "care.mold"))],
            measures: [Count(), Sum("cost", Main(ids, "care.cost"))],
            primary: Main(ids, "care.careDate"), range: Last(180))),
        new("近 6 個月保養次數趨勢", ["maint"], ids => Def(ids, "care", "line",
            groups: [Group(Main(ids, "care.careDate"), "month")],
            measures: [Count()], primary: Main(ids, "care.careDate"),
            range: new ReportDateRange("lastMonths", 6))),

        new("近 60 天品質異常嚴重度", ["qa"], ids => Def(ids, "quality", "donut",
            groups: [Group(Main(ids, "quality.severity"))],
            measures: [Count()], primary: Main(ids, "quality.reportDate"), range: Last(60))),
        new("近 60 天重大異常（依類型）", ["qa"], ids => Def(ids, "quality", "bar",
            groups: [Group(Main(ids, "quality.kind"))],
            measures: [Count()],
            filters: [new ReportFilter(Main(ids, "quality.severity"), "eq", Json(ids, "quality.critical"))],
            primary: Main(ids, "quality.reportDate"), range: Last(60))),

        // 以下幾張主要給儀表板卡片用：沒有自己的日期區間時由卡片指定（指標卡的期間比較、前 N 名與累計占比）
        new("訂單筆數", ["sales-manager", "sales"], ids => Def(ids, "order", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "order.orderDate"), range: null)),
        new("近 6 個月訂單筆數趨勢", ["sales-manager", "sales"], ids => Def(ids, "order", "line",
            groups: [Group(Main(ids, "order.orderDate"), "month")],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: new ReportDateRange("lastMonths", 6))),
        new("近 90 天各客戶訂單筆數", ["sales-manager", "sales"], ids => Def(ids, "order", "bar",
            groups: [Group(new ReportFieldRef("main", 0, ids.FieldIds["order.customer"]))],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: Last(90))),
        new("報修筆數", ["maint", "floor"], ids => Def(ids, "repair", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "repair.reportDate"), range: null)),
        new("品質異常筆數", ["qa"], ids => Def(ids, "quality", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "quality.reportDate"), range: null)),
        new("近 3 個月品質異常筆數趨勢", ["qa"], ids => Def(ids, "quality", "line",
            groups: [Group(Main(ids, "quality.reportDate"), "month")],
            measures: [Count()], primary: Main(ids, "quality.reportDate"), range: new ReportDateRange("lastMonths", 3))),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];

    private static ReportDefinition Def(
        TemplateIdMap ids, string module, string viz, List<ReportGroup> groups, List<ReportMeasure> measures,
        ReportFieldRef primary, ReportDateRange? range, ReportChild? child = null,
        List<ReportFieldRef>? columns = null, List<ReportFilter>? filters = null) =>
        new(ReportDefinition.CurrentVersion, ids.ModuleKeys[module], child, columns ?? [], groups, measures,
            filters ?? [], primary, range, [], viz);

    private static ReportFieldRef Main(TemplateIdMap ids, string field) => new("main", ids.FieldIds[field], null);

    private static ReportFieldRef Child(TemplateIdMap ids, string field) => new("child", ids.FieldIds[field], null);

    private static ReportGroup Group(ReportFieldRef field, string? bucket = null) => new(field, bucket);

    private static ReportMeasure Count() => new("count", "countMain", null, null);

    private static ReportMeasure Sum(string id, ReportFieldRef field) => new(id, "sum", field, null);

    private static ReportDateRange Last(int days) => new("lastDays", days);

    private static System.Text.Json.JsonElement Json(TemplateIdMap ids, string option) =>
        System.Text.Json.JsonSerializer.SerializeToElement($"opt_{ids.OptionIds[option]}");
}
