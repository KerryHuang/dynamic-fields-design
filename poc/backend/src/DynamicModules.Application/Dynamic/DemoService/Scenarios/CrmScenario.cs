using System.Globalization;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「CRM 與銷售」應用程式的虛構資料：8 項產品、10 家客戶的產業（客戶與聯絡人本身由 <see cref="MasterScenario"/> 建在共用主檔）、
/// 12 筆潛在客戶（含 2 筆已轉客戶與商機）、
/// 12 筆商機（六個階段都有）、18 筆活動、10 張報價單（草稿、待核准、已退回、已核准）與 4 張由報價轉出的訂單
/// （有一張報價單只轉了一部分，一張分兩次轉完）。全部走一般的建立、更新、轉換指令，權限、狀態機、帶值照常生效，
/// 步驟由 sales01、sales02 執行，核准與取消由 manager01 執行。人名、公司、電話、Email 全是虛構（Email 一律 example.com）。
/// </summary>
public sealed class CrmScenario : IDemoScenario
{
    public string Key => "crm";
    public int Seed => 20261107;

    private const string Manager = "manager01";
    private static readonly string[] Reps = ["sales01", "sales02"];

    private static readonly (string Sku, string Name, string Unit, int Price)[] Products =
    [
        ("P-2001", "工業感測器", "個", 1200), ("P-2002", "可程式控制器", "台", 8600), ("P-2003", "伺服馬達", "具", 5400),
        ("P-2004", "電源供應器", "個", 950), ("P-2005", "人機介面面板", "台", 12800), ("P-2006", "安裝支架組", "組", 480),
        ("P-2007", "訊號線材", "條", 160), ("P-2008", "年度維護服務", "式", 24000)
    ];

    private static readonly string[] Companies =
    [
        "晨曦機械", "遠見電子", "新航物流", "綠野食品", "恆展工業", "藍海貿易", "巨峰營造", "明日設計", "安泰五金", "雲端製造"
    ];

    private static readonly string[] Industries = ["manufacturing", "tech", "service", "retail", "manufacturing", "retail", "service", "tech", "retail", "manufacturing"];

    private static readonly string[] Surnames = ["林", "陳", "王", "張", "李", "黃", "吳", "劉", "蔡", "楊"];
    private static readonly string[] GivenNames = ["怡君", "家豪", "雅婷", "志明", "淑芬", "建宏", "佩珊", "俊傑", "美玲", "宗翰"];

    private static readonly string[] Stages = ["prospecting", "qualification", "proposal", "negotiation", "won"];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var rnd = plan.Random;
        DateTime After(DateTime t, int days = 1) => plan.At(DateOnly.FromDateTime(t).AddDays(days), t);
        string Rep(int i) => Reps[i % Reps.Length];

        // ---- 產品（主管建立）----
        var products = new List<DemoRecord>();
        var productStart = plan.At(today.AddDays(-75));
        for (var i = 0; i < Products.Length; i++)
        {
            var p = Products[i];
            products.Add(plan.Create("crm-product", After(productStart, 0), Manager, $"產品 {p.Sku} 建立", ctx => Create(ctx, "product",
                ("sku", p.Sku), ("name", p.Name), ("unit", p.Unit), ("price", (decimal)p.Price), ("active", true))));
        }

        // ---- 客戶：共用主檔往來對象（MasterScenario 建立，角色客戶或兩者），CRM 補上自己的「產業」----
        var customers = plan.Records[MasterScenario.PartnerModule].Where(p => (string?)p.Facts["role"] != "supplier").Take(Companies.Length).ToList();
        for (var i = 0; i < customers.Count; i++)
        {
            var n = i;
            plan.Update(customers[i], plan.At(today.AddDays(-70 + i)), Rep(i), $"客戶 {Companies[i]} 設定產業", (ctx, r) =>
                new UpdateRecordRequest(r.Id, ctx.Crm!.Values(MasterScenario.PartnerSymbol,
                    ("industry", ctx.Crm.Opt(MasterScenario.PartnerSymbol, Industries[n]))), r.RowVersion));
        }

        // ---- 潛在客戶：4 新線索、3 已聯繫、3 已確認（其中 2 筆轉客戶與商機）、2 流失 ----
        string[] leadStates = ["new", "new", "new", "new", "contacted", "contacted", "contacted", "qualified", "qualified", "qualified", "lost", "lost"];
        var leadPath = new Dictionary<string, string[]>
        {
            ["new"] = [], ["contacted"] = ["contacted"], ["qualified"] = ["contacted", "qualified"], ["lost"] = ["contacted", "lost"]
        };
        string[] sources = ["web", "referral", "exhibition", "coldcall"];
        var newCustomers = new List<DemoRecord>();
        var leadOpportunities = new List<DemoRecord>();
        for (var i = 0; i < leadStates.Length; i++)
        {
            var n = i;
            var created = plan.At(today.AddDays(-50 + i * 2));
            var lead = plan.Create("crm-lead", created, Rep(i), $"潛在客戶 {i + 1} 建立", ctx => Create(ctx, "lead",
                ("name", $"{Surnames[(n + 3) % 10]}{GivenNames[(n + 5) % 10]}"), ("company", $"{Companies[n % 10]}分公司"),
                ("phone", $"0922-555-{200 + n * 11:000}"), ("email", $"lead{n + 1}@example.com"),
                ("source", ctx.Crm!.Opt("lead", sources[n % sources.Length])), ("status", ctx.Crm.Opt("lead", "new")), ("owner", Rep(n)),
                ("note", "展覽留下名片，後續追蹤")));
            var at = created;
            foreach (var next in leadPath[leadStates[i]])
            {
                at = After(at);
                var target = next;
                plan.Update(lead, at, Rep(i), $"潛在客戶 {i + 1} → {next}", (ctx, r) =>
                    new UpdateRecordRequest(r.Id, ctx.Crm!.Values("lead", ("status", ctx.Crm.Opt("lead", target))), r.RowVersion));
            }
            if (leadStates[i] == "qualified" && newCustomers.Count < 2)
            {
                var convertAt = After(at);
                plan.Refresh(lead, convertAt, Rep(i), $"潛在客戶 {i + 1} 重讀");
                newCustomers.Add(plan.Convert(MasterScenario.PartnerModule, After(convertAt, 0), Rep(i), $"潛在客戶 {i + 1} 轉客戶", ctx =>
                    new ConvertRecordRequest(lead.Id, "crm-lead-to-customer", null, null, null)));
                leadOpportunities.Add(plan.Convert("crm-opportunity", After(convertAt, 0), Rep(i), $"潛在客戶 {i + 1} 轉商機", ctx =>
                    new ConvertRecordRequest(lead.Id, "crm-lead-to-opportunity", null, null, null)));
            }
        }

        // ---- 商機：六個階段都有。階段只能照狀態機一步步推。----
        // 最後兩組不是轉出來的：先 0..4 個階段的路徑，再補一筆失敗
        var opportunities = new List<DemoRecord>(leadOpportunities);
        (string Name, int Depth, bool Lost, int Amount)[] opportunitySpecs =
        [
            ("產線自動化升級案", 0, false, 480000), ("倉儲監控導入", 1, false, 250000), ("設備汰換專案", 1, false, 360000),
            ("工廠儀控整合", 2, false, 820000), ("維運服務續約", 2, false, 288000), ("包裝線改善案", 2, false, 540000),
            ("智慧電表佈建", 3, false, 690000), ("廠區能源管理", 3, false, 910000), ("控制器批量採購", 4, false, 430000),
            ("年度保養合約", 4, false, 192000), ("展示場設備案", 1, true, 150000)
        ];
        var opportunityCustomers = new Dictionary<DemoRecord, DemoRecord>();
        for (var i = 0; i < opportunitySpecs.Length; i++)
        {
            var spec = opportunitySpecs[i];
            var n = i;
            var customer = customers[i % customers.Count];
            var created = plan.At(today.AddDays(-45 + i * 2));
            var opp = plan.Create("crm-opportunity", created, Rep(i), $"商機 {spec.Name} 建立", ctx => Create(ctx, "opportunity",
                ("name", spec.Name), ("customer", customer.Id), ("stage", ctx.Crm!.Opt("opportunity", "prospecting")),
                ("amount", (decimal)spec.Amount), ("closeDate", Iso(today.AddDays(10 + n * 5))), ("owner", Rep(n))));
            opportunities.Add(opp);
            opportunityCustomers[opp] = customer;
            var at = created;
            for (var depth = 1; depth <= spec.Depth; depth++)
            {
                at = After(at);
                var stage = Stages[depth];
                plan.Update(opp, at, Rep(i), $"商機 {spec.Name} → {stage}", (ctx, r) =>
                    new UpdateRecordRequest(r.Id, ctx.Crm!.Values("opportunity", ("stage", ctx.Crm.Opt("opportunity", stage))), r.RowVersion));
            }
            if (spec.Lost)
            {
                at = After(at);
                plan.Update(opp, at, Rep(i), $"商機 {spec.Name} → lost", (ctx, r) =>
                    new UpdateRecordRequest(r.Id, ctx.Crm!.Values("opportunity", ("stage", ctx.Crm.Opt("opportunity", "lost"))), r.RowVersion));
            }
        }

        // ---- 活動 ----
        string[] kinds = ["call", "visit", "email", "meeting"];
        for (var i = 0; i < 18; i++)
        {
            var n = i;
            var date = today.AddDays(-rnd.Next(2, 40));
            var customer = customers[i % customers.Count];
            plan.Create("crm-activity", plan.At(date), Rep(i), $"活動 {i + 1} 建立", ctx => Create(ctx, "activity",
                ("subject", n % 2 == 0 ? "確認需求與預算" : "回覆規格問題"), ("kind", ctx.Crm!.Opt("activity", kinds[n % kinds.Length])),
                ("date", Iso(date)), ("customer", customer.Id), ("owner", Rep(n)), ("done", n % 3 != 0)));
        }

        // ---- 報價單 ----
        // Status: draft | pending | rejected | approved；Orders：每張報價單轉出的訂單（每張訂單列出各明細列要轉的數量，0 表示不轉）
        var quoteSpecs = new (string Status, int[][] Orders, string? OrderEnd)[]
        {
            ("approved", [[6, 2], [4, 0]], "completed"), // 0：分兩次轉完，第一張已完成、第二張出貨中
            ("approved", [[5, 0]], "shipped"),           // 1：只轉了一部分
            ("approved", [], null),                      // 2：核准未轉
            ("approved", [[10, 3]], "cancelled"),        // 3：一次轉完後訂單取消
            ("approved", [], null),                      // 4：核准未轉
            ("pending", [], null), ("pending", [], null),
            ("rejected", [], null),
            ("draft", [], null), ("draft", [], null)
        };
        var quoteLineCounts = new[] { 2, 2, 3, 2, 2, 2, 3, 2, 2, 2 };

        var quotes = new List<DemoRecord>();
        for (var i = 0; i < quoteSpecs.Length; i++)
        {
            var (status, orders, orderEnd) = quoteSpecs[i];
            var n = i;
            var customer = customers[(i + 2) % customers.Count];
            var created = plan.At(today.AddDays(-34 + i * 2));
            var rep = Rep(i);
            var lineQty = new[] { 10, 5, 4 };
            var picks = Enumerable.Range(0, quoteLineCounts[i]).Select(k => (i + k * 3) % Products.Length).ToArray();

            DemoRecord quote;
            var t = created;
            if (i is 3 or 4)
            {
                // 兩張報價單由商機轉出（提案、議價階段），再補明細。每一步的時間都接在前一步之後
                var source = opportunities[i == 3 ? 3 + leadOpportunities.Count : 6 + leadOpportunities.Count];
                t = After(t, 0);
                plan.Refresh(source, t, rep, $"商機重讀 {i}");
                t = After(t, 0);
                quote = plan.Convert("crm-quote", t, rep, $"商機轉報價單 {i + 1}", ctx =>
                    new ConvertRecordRequest(source.Id, "crm-opportunity-to-quote", null, null, null));
                t = After(t, 0);
                plan.Refresh(quote, t, rep, $"報價單 {i + 1} 重讀");
                t = After(t, 0);
                plan.Update(quote, t, rep, $"報價單 {i + 1} 加明細", (ctx, r) => new UpdateRecordRequest(r.Id, [], r.RowVersion,
                    Lines(ctx, picks, lineQty, products)));
            }
            else
            {
                quote = plan.Create("crm-quote", created, rep, $"報價單 {i + 1} 建立", ctx =>
                {
                    var values = ctx.Crm!.Values("quote",
                        ("customer", customer.Id), ("quoteDate", Iso(DateOnly.FromDateTime(created))),
                        ("validUntil", Iso(DateOnly.FromDateTime(created).AddDays(30))), ("owner", rep),
                        ("remark", n % 2 == 0 ? "含運費，付款條件月結 30 天" : "交期 4 週"));
                    return new CreateRecordRequest(ctx.Crm.Ids.ModuleKeys["quote"], values, Details: Lines(ctx, picks, lineQty, products));
                });
            }
            quotes.Add(quote);
            if (status == "draft") continue;

            var at = After(t, 1);
            plan.Refresh(quote, at, rep, $"報價單 {i + 1} 重讀");
            at = After(at, 0);
            plan.Update(quote, at, rep, $"報價單 {i + 1} 送核准", Status("quote", "pending"));
            if (status == "pending") continue;

            at = After(at);
            plan.Update(quote, at, Manager, $"報價單 {i + 1} " + (status == "rejected" ? "退回" : "核准"), Status("quote", status == "rejected" ? "rejected" : "approved"));
            if (status == "rejected") continue;

            foreach (var lines in orders)
            {
                var lineQuantities = lines;
                at = After(at, 2);
                plan.Refresh(quote, at, rep, $"報價單 {i + 1} 轉單前重讀");
                at = After(at, 0);
                var order = plan.Convert("crm-order", at, rep, $"報價單 {i + 1} 轉訂單", ctx =>
                {
                    var lineKey = ctx.Crm!.Ids.ModuleKeys["quote-line"];
                    var rows = quote.DetailRows[lineKey];
                    var take = lineQuantities.Select((q, k) => (q, k)).Where(x => x.q > 0 && x.k < rows.Count)
                        .Select(x => new ConvertLineInput(rows[x.k].Id, x.q)).ToList();
                    return new ConvertRecordRequest(quote.Id, "crm-quote-to-order", null, null, take);
                });
                if (lines == orders[^1] && orderEnd is not null)
                {
                    foreach (var step in OrderPath(orderEnd))
                    {
                        var (target, who) = step;
                        at = After(at);
                        plan.Refresh(order, at, who, "訂單重讀");
                        at = After(at, 0);
                        plan.Update(order, at, who, $"訂單 → {target}", Status("order", target));
                    }
                }
            }
        }
    }

    /// <summary>訂單狀態路徑：已確認 → 已出貨 → 已完成，或已確認 → 已取消（取消只有主管能做）。</summary>
    private static IEnumerable<(string Status, string User)> OrderPath(string end) => end switch
    {
        "shipped" => [("shipped", "sales01")],
        "completed" => [("shipped", "sales01"), ("completed", "sales01")],
        "cancelled" => [("cancelled", Manager)],
        _ => []
    };

    private static Dictionary<string, List<DetailRowInput>> Lines(DemoContext ctx, int[] picks, int[] quantities, List<DemoRecord> products) =>
        new()
        {
            [ctx.Crm!.Ids.ModuleKeys["quote-line"]] = picks.Select((p, k) => new DetailRowInput(null, ctx.Crm.Values("quote-line",
                ("product", products[p].Id), ("qty", (decimal)quantities[k % quantities.Length])), null)).ToList()
        };

    private static CreateRecordRequest Create(DemoContext ctx, string module, params (string Code, object? Value)[] items) =>
        new(ctx.Crm!.Ids.ModuleKeys[module], ctx.Crm.Values(module, items));

    private static Func<DemoContext, DemoRecord, UpdateRecordRequest> Status(string module, string code) =>
        (ctx, r) => new UpdateRecordRequest(r.Id, ctx.Crm!.Values(module, ("status", ctx.Crm.Opt(module, code))), r.RowVersion);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
