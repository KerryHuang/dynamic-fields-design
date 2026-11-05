using System.Globalization;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 訂單 40 張（今天前 1–79 天，每兩天一張）：sales01、sales02 交替建立，manager01 推進狀態。
/// 文中「第 n 張」從 1 起算，對應 i = n − 1。
/// </summary>
public sealed class OrderScenario : IDemoScenario
{
    public const int Count = 40;

    public string Key => "order";
    public int Seed => 20261101;

    private const string Manager = "manager01";

    private static readonly (string No, string Name, string Unit)[] Items =
    [
        ("P-1001", "外殼上蓋", "pcs"), ("P-1002", "外殼下蓋", "pcs"), ("P-1003", "按鍵組", "set"),
        ("P-1004", "電池蓋", "pcs"), ("P-1005", "固定支架", "pcs"), ("P-1006", "旋鈕", "pcs"),
        ("P-1007", "前面板", "pcs"), ("P-1008", "底座", "pcs"), ("P-1009", "導光板", "pcs"),
        ("P-1010", "塑膠粒", "kg")
    ];

    private static readonly string[] CancelReasons = ["客戶調整產品規格，整單重下", "客戶預算凍結，暫停採購"];

    /// <summary>第 3、17 張改成取消。</summary>
    private static readonly int[] Cancelled = [2, 16];

    private sealed record Line(string ItemNo, string ItemName, string Unit, int Qty, int Price);

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        var dates = Enumerable.Range(0, Count).Select(i => today.AddDays(-1 - (Count - 1 - i) * 2)).ToArray();
        var statuses = dates.Select((d, i) => StatusOf(today.DayNumber - d.DayNumber, i)).ToArray();
        // 至少 4 張「出貨中或已確認、交期已過」：最早的 2 張出貨中、2 張已確認，交期設成訂單日期 + 7
        var overdue = Enumerable.Range(0, Count).Where(i => statuses[i] == "shipping").Take(2)
            .Concat(Enumerable.Range(0, Count).Where(i => statuses[i] == "confirmed").Take(2)).ToHashSet();

        for (var i = 0; i < Count; i++)
        {
            var date = dates[i];
            var status = statuses[i];
            var creator = i % 2 == 0 ? "sales01" : "sales02";
            var customer = $"C-{rnd.Next(1, 9):000}";
            var due = overdue.Contains(i) ? date.AddDays(7) : date.AddDays(rnd.Next(7, 46));
            var lines = Enumerable.Range(0, rnd.Next(1, 7)).Select(_ =>
            {
                var item = Items[rnd.Next(Items.Length)];
                return new Line(item.No, item.Name, item.Unit, rnd.Next(1, 51), rnd.Next(50, 3001));
            }).ToList();
            var label = $"訂單第 {i + 1} 張";

            var createdAt = plan.At(date);
            var order = plan.Create("order", createdAt, creator, $"{label} 建立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["order"],
                ctx.Values("order",
                    ("customer", customer), ("orderDate", Iso(date)), ("dueDate", Iso(due)),
                    ("status", ctx.Opt("order", "draft")), ("salesRep", creator)),
                Details: new() { [ctx.Ids.ModuleKeys["line"]] = lines.Select(l => new DetailRowInput(null, ctx.Values("line",
                    ("itemNo", l.ItemNo), ("itemName", l.ItemName), ("qty", (decimal)l.Qty),
                    ("unit", ctx.Opt("line", l.Unit)), ("price", (decimal)l.Price)), null)).ToList() }));
            order.Facts["status"] = status;
            order.Facts["dueDate"] = due;
            order.Facts["customer"] = customer;

            // 每第 5 張在 +1 天由建立者改一列明細的數量（留下明細歷程）
            if (i % 5 == 4)
            {
                var row = rnd.Next(lines.Count);
                var qty = lines[row].Qty % 50 + 1;
                plan.Update(order, plan.At(date.AddDays(1), createdAt), creator, $"{label} 改明細數量", (ctx, r) =>
                {
                    var lineKey = ctx.Ids.ModuleKeys["line"];
                    var (id, rowVersion) = r.DetailRows[lineKey][row];
                    return new UpdateRecordRequest(r.Id, [], r.RowVersion, new()
                    {
                        [lineKey] = [new DetailRowInput(id, ctx.Values("line", ("qty", (decimal)qty)), rowVersion)]
                    });
                });
            }

            if (status == "draft") continue;
            var confirmedAt = plan.At(date.AddDays(1), createdAt);
            plan.Update(order, confirmedAt, Manager, $"{label} 確認", SetStatus("confirmed"));
            if (status == "confirmed") continue;

            if (status == "cancel")
            {
                var reason = CancelReasons[Array.IndexOf(Cancelled, i)];
                plan.Update(order, plan.At(date.AddDays(2), confirmedAt), Manager, $"{label} 取消", (ctx, r) =>
                    new UpdateRecordRequest(r.Id, ctx.Values("order", ("status", ctx.Opt("order", "cancel")), ("cancelReason", reason)), r.RowVersion));
                continue;
            }

            var shippedAt = plan.At(date.AddDays(3), confirmedAt);
            plan.Update(order, shippedAt, Manager, $"{label} 出貨", SetStatus("shipping"));
            if (status == "closed")
                plan.Update(order, plan.At(due, shippedAt), Manager, $"{label} 結案", SetStatus("closed"));
        }
    }

    /// <summary>依訂單日期距今的天數：大於 45 結案（第 3、17 張取消）、20–45 出貨中、7–20 已確認、小於 7 草稿。</summary>
    private static string StatusOf(int days, int i) => days switch
    {
        > 45 => Cancelled.Contains(i) ? "cancel" : "closed",
        >= 20 => "shipping",
        >= 7 => "confirmed",
        _ => "draft"
    };

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> SetStatus(string code) => (ctx, r) =>
        new UpdateRecordRequest(r.Id, ctx.Values("order", ("status", ctx.Opt("order", code))), r.RowVersion);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
