using System.Globalization;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「進銷存」應用程式的虛構資料：8 項品號、2 個倉庫（3 家供應商、4 家客戶與他們的聯絡人由 <see cref="MasterScenario"/> 建在共用主檔，
/// 這裡替客戶補上進銷存貢獻的出貨地址）；期初盤點入庫；9 張採購單，到貨單由採購單轉出並過帳；4 張出貨單（過帳後庫存減少，其中一張出貨後被主管取消，沖回庫存）；一張盤點差異調整。
/// 採購單的「草稿 → 已確認」由套件宣告成需簽核（金額門檻流程，簽核 a5c），所以一律由採購人員送簽、依組織找主管同意：
/// 含稅總計 10,000 以上走一關（營運部主管 ops01）、100,000 以上再加第二關（總經理室主管 manager01）、不到門檻送出即確認。
/// 9 張的狀態：3 張一關核准後到貨結案、2 張一關核准的已確認（其中一張部分到貨）、1 張小額直接確認、1 張大額兩關核准、
/// 1 張大額停在第二關（記錄仍是草稿，manager01 待簽）、1 張草稿。
/// 庫存異動由套件的事件在過帳、取消時自動寫入，全部走一般的建立、更新、轉換指令，權限與狀態機照常生效。
/// 期初庫存每品每倉 500，出貨量遠低於庫存，所以不會出現負庫存。公司、人名、電話、Email 全是虛構（Email 一律 example.com）。
/// </summary>
public sealed class InventoryScenario : IDemoScenario
{
    public string Key => "inventory";
    public int Seed => 20261110;

    private const string Manager = "ops01";
    private const string Executive = "manager01";
    private const string Buyer = "buyer01";
    private const string Keeper = "keeper01";

    private static readonly (string Name, string Category, string Unit, int Price, int Safety)[] Items =
    [
        ("鋼材 S45C 板", "raw", "kg", 85, 200), ("合金鋼棒", "raw", "kg", 120, 150), ("標準導柱", "part", "pcs", 180, 50),
        ("標準導套", "part", "pcs", 150, 50), ("頂針組", "part", "set", 640, 20), ("潤滑油", "consumable", "L", 260, 30),
        ("拋光砂紙", "consumable", "pack", 95, 40), ("成品模仁", "finished", "pcs", 5200, 5)
    ];

    private static readonly string[] Warehouses = ["主倉", "成品倉"];

    /// <param name="Supplier">供應商編號。</param>
    /// <param name="Lines">（品項編號, 數量）。</param>
    /// <param name="Receipts">到貨批次各行比例（百分比）；空陣列＝尚未到貨。</param>
    /// <param name="End">draft／confirmed／closed／cancelled／pending（送簽後只同意 Approvers 列出的關卡，停在下一關）。</param>
    /// <param name="Approvers">送簽後依序同意的人；空陣列＝不經簽核直接確認（含稅總計低於門檻）。</param>
    private sealed record PoSpec(int DaysAgo, int Supplier, (int Item, int Qty)[] Lines, int[][] Receipts, string End, string[] Approvers);

    private static readonly PoSpec[] Pos =
    [
        new(80, 0, [(0, 300), (1, 200)], [[100, 100]], "closed", [Manager]),
        new(66, 1, [(2, 100), (3, 100), (4, 40)], [[50, 50, 50], [50, 50, 50]], "closed", [Manager]),
        new(50, 2, [(5, 60), (6, 80)], [[100, 100]], "closed", [Manager]),
        new(35, 0, [(0, 400), (1, 300)], [[60, 60]], "confirmed", [Manager]),
        new(18, 1, [(2, 60), (4, 30)], [], "confirmed", [Manager]),
        new(9, 2, [(7, 10)], [], "draft", []),
        // 簽核 a5c 加的三張：小額（含稅約 5,460）直接確認；大額（約 109,200）兩關核准；大額（約 136,500）停在第二關
        new(7, 1, [(5, 20)], [], "confirmed", []),
        new(12, 0, [(7, 20)], [], "confirmed", [Manager, Executive]),
        new(4, 2, [(7, 25)], [], "pending", [Manager])
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        DateTime After(DateTime t, int days = 1) => plan.At(DateOnly.FromDateTime(t).AddDays(days), t);

        // ---- 主檔：品號、倉庫（主管建立）----
        var start = plan.At(today.AddDays(-90));
        var items = new List<DemoRecord>();
        for (var i = 0; i < Items.Length; i++)
        {
            var n = i;
            items.Add(plan.Create("inv-item", After(start, 0), Manager, $"品號 {Items[i].Name} 建立", ctx => Create(ctx, "item",
                ("name", Items[n].Name), ("category", ctx.Inventory!.Opt("item", Items[n].Category)), ("unit", Items[n].Unit),
                ("price", (decimal)Items[n].Price), ("safetyStock", (decimal)Items[n].Safety), ("active", true))));
        }
        var warehouses = new List<DemoRecord>();
        for (var i = 0; i < Warehouses.Length; i++)
        {
            var n = i;
            warehouses.Add(plan.Create("inv-warehouse", After(start, 0), Manager, $"倉庫 {Warehouses[i]} 建立", ctx => Create(ctx, "warehouse",
                ("code", $"WH-{(char)('A' + n)}"), ("name", Warehouses[n]), ("location", $"示範市工業區 {n + 1} 號廠房"), ("active", true))));
        }
        // 供應商、客戶：共用主檔往來對象（MasterScenario 建立），主管補上客戶的出貨地址
        var partners = MasterScenario.ForApp(plan, DemoCatalog.InventoryAppId);
        var suppliers = partners.Where(p => (string?)p.Facts["role"] == "supplier").ToList();
        var customers = partners.Where(p => (string?)p.Facts["role"] == "customer").ToList();
        for (var i = 0; i < customers.Count; i++)
        {
            var address = $"示範市中正路 {20 + i * 6} 號";
            plan.Update(customers[i], After(start, 0), Manager, $"客戶 {customers[i].Facts["name"]} 設定出貨地址", (ctx, r) =>
                new UpdateRecordRequest(r.Id, ctx.Inventory!.Values(MasterScenario.PartnerSymbol, ("shipAddress", address)), r.RowVersion));
        }

        // ---- 期初盤點：每品每倉 500，由倉管建立並過帳 ----
        var openingAt = plan.At(today.AddDays(-88));
        for (var w = 0; w < warehouses.Count; w++)
        {
            var wh = warehouses[w];
            var adjust = plan.Create("inv-adjust", After(openingAt, 0), Keeper, $"{Warehouses[w]} 期初盤點 建立", ctx => new CreateRecordRequest(
                ctx.Inventory!.Ids.ModuleKeys["adjust"],
                ctx.Inventory.Values("adjust", ("warehouse", wh.Id), ("adjustDate", Iso(today.AddDays(-88))),
                    ("reason", ctx.Inventory.Opt("adjust", "opening")), ("status", ctx.Inventory.Opt("adjust", "draft"))),
                Details: new()
                {
                    [ctx.Inventory.Ids.ModuleKeys["adjust-line"]] = items.Select(it => new DetailRowInput(null,
                        ctx.Inventory.Values("adjust-line", ("item", it.Id), ("qty", 500m)), null)).ToList()
                }));
            plan.Update(adjust, After(openingAt), Keeper, $"{Warehouses[w]} 期初盤點 過帳", Status("adjust", "posted"));
        }

        // ---- 採購單與到貨 ----
        for (var i = 0; i < Pos.Length; i++)
        {
            var spec = Pos[i];
            var date = today.AddDays(-spec.DaysAgo);
            var label = $"採購單第 {i + 1} 張";
            var supplier = suppliers[spec.Supplier];
            var wh = warehouses[0];
            var at = plan.At(date);
            var po = plan.Create("inv-po", at, Buyer, $"{label} 建立", ctx => new CreateRecordRequest(
                ctx.Inventory!.Ids.ModuleKeys["po"],
                ctx.Inventory.Values("po", ("supplier", supplier.Id), ("warehouse", wh.Id), ("orderDate", Iso(date)),
                    ("expectedDate", Iso(date.AddDays(14))), ("status", ctx.Inventory.Opt("po", "draft"))),
                Details: new()
                {
                    [ctx.Inventory.Ids.ModuleKeys["po-line"]] = spec.Lines.Select(l => new DetailRowInput(null,
                        ctx.Inventory.Values("po-line", ("item", items[l.Item].Id), ("qty", (decimal)l.Qty)), null)).ToList()
                }));
            if (spec.End == "draft") continue;

            at = After(at, 0);
            plan.Run(at, Buyer, $"{label} 送簽", (ctx, ct) => DemoApprovalSteps.Submit(ctx, po,
                ctx.Inventory!.Ids.OptionIds["po.confirmed"], spec.Approvers.Length == 0 ? null : "請核准採購", ct));
            foreach (var approver in spec.Approvers)
            {
                at = After(at);
                plan.Run(at, approver, $"{label} {approver} 同意", (ctx, ct) => DemoApprovalSteps.Approve(ctx, po, "同意", ct));
            }
            if (spec.End == "pending") continue;
            foreach (var percents in spec.Receipts)
            {
                var pcts = percents;
                at = After(at, 3);
                plan.Refresh(po, at, Keeper, $"{label} 轉到貨單前重讀");
                at = After(at, 0);
                var receipt = plan.Convert("inv-receipt", at, Keeper, $"{label} 轉到貨單", ctx =>
                {
                    var rows = po.DetailRows[ctx.Inventory!.Ids.ModuleKeys["po-line"]];
                    var take = pcts.Select((p, k) => (p, k)).Where(x => x.p > 0 && x.k < rows.Count)
                        .Select(x => new ConvertLineInput(rows[x.k].Id, Math.Max(1, spec.Lines[x.k].Qty * x.p / 100))).ToList();
                    return new ConvertRecordRequest(po.Id, "inv-po-to-receipt", null, null, take);
                });
                at = After(at, 0);
                plan.Update(receipt, at, Keeper, $"{label} 到貨過帳", Status("receipt", "posted"));
            }
            if (spec.End == "closed")
            {
                at = After(at);
                plan.Refresh(po, at, Buyer, $"{label} 結案前重讀");
                at = After(at, 0);
                plan.Update(po, at, Buyer, $"{label} 結案", Status("po", "closed"));
            }
        }

        // ---- 出貨單：4 張，過帳後庫存減少；第 2 張過帳後被主管取消（沖回庫存）----
        var shipments = new (int DaysAgo, int Customer, (int Item, int Qty)[] Lines, bool Cancel)[]
        {
            (40, 0, [(2, 20), (3, 20)], false), (30, 1, [(7, 2), (4, 10)], true), (22, 2, [(0, 80)], false), (11, 3, [(2, 15), (5, 10)], false)
        };
        for (var i = 0; i < shipments.Length; i++)
        {
            var s = shipments[i];
            var date = today.AddDays(-s.DaysAgo);
            var label = $"出貨單第 {i + 1} 張";
            var customer = customers[s.Customer];
            var wh = warehouses[i == 1 ? 1 : 0];
            var at = plan.At(date);
            var shipment = plan.Create("inv-shipment", at, Keeper, $"{label} 建立", ctx => new CreateRecordRequest(
                ctx.Inventory!.Ids.ModuleKeys["shipment"],
                ctx.Inventory.Values("shipment", ("customer", customer.Id), ("warehouse", wh.Id), ("shipDate", Iso(date)),
                    ("status", ctx.Inventory.Opt("shipment", "draft"))),
                Details: new()
                {
                    [ctx.Inventory.Ids.ModuleKeys["shipment-line"]] = s.Lines.Select(l => new DetailRowInput(null,
                        ctx.Inventory.Values("shipment-line", ("item", items[l.Item].Id), ("qty", (decimal)l.Qty)), null)).ToList()
                }));
            at = After(at, 0);
            plan.Update(shipment, at, Keeper, $"{label} 出貨過帳", Status("shipment", "shipped"));
            if (!s.Cancel) continue;
            at = After(at, 2);
            plan.Refresh(shipment, at, Manager, $"{label} 取消前重讀");
            at = After(at, 0);
            plan.Update(shipment, at, Manager, $"{label} 取消（沖回庫存）", Status("shipment", "cancelled"));
        }

        // ---- 盤點差異調整：主倉的潤滑油盤少 3 L，拋光砂紙盤多 2 包 ----
        var countAt = plan.At(today.AddDays(-6));
        var count = plan.Create("inv-adjust", countAt, Keeper, "盤點差異 建立", ctx => new CreateRecordRequest(
            ctx.Inventory!.Ids.ModuleKeys["adjust"],
            ctx.Inventory.Values("adjust", ("warehouse", warehouses[0].Id), ("adjustDate", Iso(today.AddDays(-6))),
                ("reason", ctx.Inventory.Opt("adjust", "count")), ("status", ctx.Inventory.Opt("adjust", "draft"))),
            Details: new()
            {
                [ctx.Inventory.Ids.ModuleKeys["adjust-line"]] =
                [
                    new DetailRowInput(null, ctx.Inventory.Values("adjust-line", ("item", items[5].Id), ("qty", -3m), ("note", "盤點短少")), null),
                    new DetailRowInput(null, ctx.Inventory.Values("adjust-line", ("item", items[6].Id), ("qty", 2m), ("note", "盤點多出")), null)
                ]
            }));
        plan.Update(count, After(countAt, 0), Keeper, "盤點差異 過帳", Status("adjust", "posted"));
    }

    private static CreateRecordRequest Create(DemoContext ctx, string module, params (string Code, object? Value)[] items) =>
        new(ctx.Inventory!.Ids.ModuleKeys[module], ctx.Inventory.Values(module, items));

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string module, string code) =>
        (ctx, r) => new UpdateRecordRequest(r.Id, ctx.Inventory!.Values(module, ("status", ctx.Inventory.Opt(module, code))), r.RowVersion);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
