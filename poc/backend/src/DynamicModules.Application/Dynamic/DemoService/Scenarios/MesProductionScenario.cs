using System.Globalization;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「MES 模具廠」的工令到出貨：從前面情境建立的訂單挑 9 張開工令（生管建立、發放，現場報工，生管完工，生管出貨）。
/// 各種結局都有：全數出貨、只出一部分、短交結案、進行中、逾期未完工、暫停（缺料）、已發放未開工、草稿、取消。
/// 現場每道工序報工一次（良品數 = 工令數量），粗加工有不良品；其中一張有一筆報錯後沖銷再重報。
/// 全部走一般的建立、更新與沖銷指令，狀態機、檢查、事件與彙總照常生效；步驟一律晚於訂單的建立，且 At ≥ 今天的步驟會被丟掉。
/// </summary>
public sealed class MesProductionScenario : IDemoScenario
{
    public string Key => "mes-production";
    public int Seed => 20261109;

    private const string Planner = "planner01";
    private const string Floor = "floor01";
    private const int FinishedItem = 7;      // InventoryScenario.Items 的「成品模仁」
    private const int FinishedWarehouse = 1; // InventoryScenario.Warehouses 的「成品倉」

    private static readonly string[] Processes = ["material", "rough", "cnc", "edm", "polish", "assemble"];
    private static readonly string[] Stations = ["SAW-1", "MILL-2", "CNC-3", "EDM-4", "POL-1", "ASM-1"];
    private static readonly (string No, string Name)[] Items =
    [
        ("GP-01", "導柱"), ("GB-02", "導套"), ("EP-03", "頂針"), ("MB-04", "模板"), ("CR-05", "模仁"), ("SL-06", "滑塊"), ("RN-07", "澆口套"), ("CK-08", "冷卻水路板"), ("LF-09", "頂出板")
    ];

    private enum End { Shipped, ShippedPartly, ShortClose, Running, Overdue, Hold, Released, Draft, Cancelled }

    private static readonly End[] Ends =
    [
        End.Shipped, End.ShippedPartly, End.ShortClose, End.Shipped, End.Running, End.Overdue, End.Hold, End.Released, End.Cancelled, End.Draft
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        DateTime After(DateTime t, int days = 1) => plan.At(DateOnly.FromDateTime(t).AddDays(days), t);

        // 較早的訂單先開工（出貨與完工才有時間走完）：標準訂單取出貨中／已結案，報價轉出的訂單取已確認
        var standard = plan.Records.GetValueOrDefault("order", []).Where(o => o.Facts["status"] is "shipping" or "closed")
            .OrderBy(o => o.Date).Take(6);
        var fromQuote = plan.Records.GetValueOrDefault(MesQuoteScenario.OrderCode, []).OrderBy(o => o.Date).Take(4);
        var orders = standard.Concat(fromQuote).ToList();

        for (var i = 0; i < Math.Min(orders.Count, Ends.Length); i++)
        {
            var n = i;
            var order = orders[i];
            var end = Ends[i];
            var item = Items[i % Items.Length];
            var qty = rnd.Next(2, 13) * 10;
            var opCount = rnd.Next(4, 7);
            var customer = (DemoRecord)order.Facts["customer"]!;
            var label = $"工令第 {i + 1} 張";

            var createdAt = plan.At(order.Date.AddDays(1 + rnd.Next(2)));
            var start = DateOnly.FromDateTime(createdAt).AddDays(2);
            var due = end == End.Overdue ? today.AddDays(-4 - rnd.Next(10)) : DateOnly.FromDateTime(createdAt).AddDays(25 + rnd.Next(15));
            if (due <= start) due = start.AddDays(5);
            var wo = plan.Create("mes-workorder", createdAt, Planner, $"{label} 建立", ctx => new CreateRecordRequest(
                ctx.Ids.ModuleKeys["workorder"],
                ctx.Values("workorder", ("order", order.Id), ("itemNo", item.No), ("itemName", item.Name), ("qty", qty), ("unit", "pcs"),
                    ("plannedStart", Iso(start)), ("dueDate", Iso(due)), ("priority", ctx.Opt("workorder", n % 4 == 1 ? "urgent" : "normal")),
                    ("status", ctx.Opt("workorder", "draft")), ("finalSeq", opCount)),
                Details: new()
                {
                    [ctx.Ids.ModuleKeys["wo-op"]] = Enumerable.Range(0, opCount).Select(k => new DetailRowInput(null, ctx.Values("wo-op",
                        ("process", ctx.Opt("wo-op", Processes[k])), ("workstation", Stations[k]), ("plannedHours", 2m + k),
                        ("plannedStart", Iso(start.AddDays(k * 2))), ("plannedEnd", Iso(start.AddDays(k * 2 + 1)))), null)).ToList()
                }));
            if (end == End.Draft) continue;

            var at = After(createdAt);
            if (end == End.Cancelled)
            {
                plan.Update(wo, at, Planner, $"{label} 取消", Status("cancelled"));
                continue;
            }
            plan.Update(wo, at, Planner, $"{label} 發放", Status("released"));
            if (end == End.Released) continue;

            // 報工：每道工序一次；Hold 在第 2 道後暫停，Running／Overdue 只做到一半
            var reportOps = end switch
            {
                End.Hold => 2,
                End.Running => opCount / 2,
                End.Overdue => opCount - 1,
                _ => opCount
            };
            var good = qty;
            for (var k = 0; k < reportOps; k++)
            {
                var seq = k + 1;
                var isFinal = seq == opCount;
                var thisGood = isFinal && end == End.ShortClose ? qty * 8 / 10 : good;
                var defect = k == 1 ? 2 : 0;
                at = After(at, k == 0 ? 1 : 2);
                var date = DateOnly.FromDateTime(at);
                var process = Processes[k];
                if (i == 0 && k == 2)
                {
                    // 報錯一筆再沖銷：此工序的累計良品仍然只算重報的那一筆
                    var wrong = plan.Create("mes-report", at, Floor, $"{label} 第 {seq} 道工序報錯", ctx => Report(ctx, wo, seq, process, date, qty - 5, 0));
                    at = After(at, 0);
                    plan.Reverse(wrong, at, Floor, $"{label} 沖銷報錯的報工");
                    at = After(at, 0);
                }
                plan.Create("mes-report", at, Floor, $"{label} 第 {seq} 道工序報工", ctx => Report(ctx, wo, seq, process, date, thisGood, defect));
            }
            if (end == End.Hold)
            {
                at = After(at);
                plan.Refresh(wo, at, Planner, $"{label} 暫停前重讀");
                at = After(at, 0);
                plan.Update(wo, at, Planner, $"{label} 暫停（缺料）", (ctx, r) => new UpdateRecordRequest(r.Id,
                    ctx.Values("workorder", ("status", ctx.Opt("workorder", "hold")), ("holdReason", ctx.Opt("workorder", "shortage"))), r.RowVersion));
                continue;
            }
            if (end is End.Running or End.Overdue) continue;

            // 完工：短交結案要同時勾選「短交結案」
            at = After(at);
            plan.Refresh(wo, at, Planner, $"{label} 完工前重讀");
            at = After(at, 0);
            var shortClose = end == End.ShortClose;
            plan.Update(wo, at, Planner, $"{label} 完工", (ctx, r) => new UpdateRecordRequest(r.Id,
                shortClose
                    ? ctx.Values("workorder", ("status", ctx.Opt("workorder", "done")), ("shortClose", true))
                    : ctx.Values("workorder", ("status", ctx.Opt("workorder", "done"))), r.RowVersion));

            // 出貨：全數出貨分兩批，只出一部分的出 60%，短交結案出完工量
            var completed = shortClose ? qty * 8 / 10 : qty;
            var batches = end switch
            {
                End.Shipped => new[] { completed * 6 / 10, completed - completed * 6 / 10 },
                End.ShippedPartly => [completed * 6 / 10],
                _ => new[] { completed }
            };
            // 第一張工令另有一張樣品出貨單：出貨扣庫存後被生管取消（沖回庫存），所以放在正式出貨之前，不佔完工量
            if (i == 0)
            {
                at = After(at, 1);
                var sample = CreateShipment(plan, wo, customer, DateOnly.FromDateTime(at), 1, "快遞", at, $"{label} 樣品出貨單");
                at = After(at, 0);
                plan.Update(sample, at, Planner, $"{label} 樣品出貨", Status("shipment", "shipped"));
                at = After(at, 1);
                plan.Refresh(sample, at, Planner, $"{label} 樣品取消前重讀");
                at = After(at, 0);
                plan.Update(sample, at, Planner, $"{label} 樣品出貨取消（沖回庫存）", Status("shipment", "cancelled"));
            }

            for (var b = 0; b < batches.Length; b++)
            {
                var shipQty = batches[b];
                at = After(at, b == 0 ? 1 : 3);
                var shipDate = DateOnly.FromDateTime(at);
                var shipment = CreateShipment(plan, wo, customer, shipDate, shipQty, b == 0 ? "貨運" : "自取", at, $"{label} 第 {b + 1} 批出貨單");
                at = After(at, 0);
                plan.Update(shipment, at, Planner, $"{label} 第 {b + 1} 批出貨", Status("shipment", "shipped"));
            }
        }
    }

    private static CreateRecordRequest Report(DemoContext ctx, DemoRecord wo, int seq, string process, DateOnly date, int good, int defect) =>
        new(ctx.Ids.ModuleKeys["report"], defect > 0
            ? ctx.Values("report", ("workOrder", wo.Id), ("opSeq", seq), ("process", ctx.Opt("report", process)), ("reportDate", Iso(date)),
                ("worker", Floor), ("goodQty", good), ("defectQty", defect), ("defectReason", ctx.Opt("report", "scratch")))
            : ctx.Values("report", ("workOrder", wo.Id), ("opSeq", seq), ("process", ctx.Opt("report", process)), ("reportDate", Iso(date)),
                ("worker", Floor), ("goodQty", good), ("defectQty", 0)));

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string code) => Status("workorder", code);

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string module, string code) =>
        (ctx, r) => new UpdateRecordRequest(r.Id, ctx.Values(module, ("status", ctx.Opt(module, code))), r.RowVersion);

    /// <summary>
    /// 出貨單。進銷存同時開啟時，「出貨扣庫存」整合要求每張出貨單帶庫存品項與倉庫：一律從進銷存情境建立的「成品模仁」「成品倉」扣
    /// （期初每品每倉 500，所有工令的出貨量加起來遠小於此）。進銷存情境排在本情境之前，所以這兩筆記錄的 Id 執行時已回填。
    /// </summary>
    private static DemoRecord CreateShipment(DemoPlan plan, DemoRecord wo, DemoRecord customer, DateOnly date, int qty, string carrier, DateTime at, string label)
    {
        var stockItem = plan.Records["inv-item"][FinishedItem];
        var stockWarehouse = plan.Records["inv-warehouse"][FinishedWarehouse];
        return plan.Create("mes-shipment", at, Planner, label, ctx => new CreateRecordRequest(
            ctx.Ids.ModuleKeys["shipment"],
            ctx.Values("shipment", ("workOrder", wo.Id), ("customer", customer.Id), ("shipDate", Iso(date)), ("qty", qty), ("carrier", carrier),
                ("stockItem", stockItem.Id), ("stockWarehouse", stockWarehouse.Id), ("status", ctx.Opt("shipment", "draft")))));
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
