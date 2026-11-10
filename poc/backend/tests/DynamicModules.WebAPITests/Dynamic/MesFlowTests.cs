using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>MES 模具廠以 Demo 的群組與帳號開啟（planner01、floor01、sales01、manager01…）：報價 → 訂單 → 工令的權限、轉換與列印。</summary>
public class MesFlowTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new(fileDatabase: true);
    private readonly HttpClient _admin;
    private JsonElement _opened;
    private long _customer;
    private readonly Dictionary<string, (Dictionary<string, string> Keys, Dictionary<string, string> Options)> _layouts = new();

    public MesFlowTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task OpenMes()
    {
        Dictionary<string, long> ids;
        using (var scope = _app.Services.CreateScope())
            ids = scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().ToDictionary(g => g.Key, g => g.Id);
        var map = new Dictionary<string, object>
        {
            ["sales"] = new { mode = "map", groupId = ids["sales"] }, ["salesManager"] = new { mode = "map", groupId = ids["sales-manager"] },
            ["floor"] = new { mode = "map", groupId = ids["floor"] }, ["maint"] = new { mode = "map", groupId = ids["maint"] },
            ["qa"] = new { mode = "map", groupId = ids["qa"] }, ["planner"] = new { mode = "map", groupId = ids["planner"] }
        };
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/mes-mold/open", new { groupMap = map });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        _opened = await JsonOf(res);
        _customer = await PartnerAsync(_admin, "宏達科技");
    }

    private string Key(string code) =>
        _opened.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private async Task<(Dictionary<string, string> Keys, Dictionary<string, string> Options)> Layout(string code)
    {
        if (_layouts.TryGetValue(code, out var cached)) return cached;
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Key(code)}"));
        string Label(JsonElement e) => e.GetProperty("label").GetProperty("zh-TW").GetString()!;
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        var keys = fields.ToDictionary(Label, f => f.GetProperty("key").GetString()!);
        var options = fields.Where(f => f.TryGetProperty("options", out _))
            .SelectMany(f => f.GetProperty("options").EnumerateArray().Select(o => (Field: Label(f), Option: o)))
            .ToDictionary(x => $"{x.Field}/{Label(x.Option)}", x => x.Option.GetProperty("value").GetString()!);
        return _layouts[code] = (keys, options);
    }

    private static long Id(JsonElement rec) => rec.GetProperty("id").GetInt64();

    private async Task<HttpResponseMessage> TryCreate(HttpClient c, string module, Dictionary<string, object?> values, object? details = null) =>
        await c.PostAsJsonAsync($"{Base}/modules/{Key(module)}/records", new { values, details });

    private async Task<JsonElement> Create(HttpClient c, string module, Dictionary<string, object?> values, object? details = null)
    {
        var res = await TryCreate(c, module, values, details);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<HttpResponseMessage> TryMove(HttpClient c, JsonElement rec, string module, string statusLabel, Dictionary<string, object?>? extra = null)
    {
        var (keys, options) = await Layout(module);
        var current = await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(rec)}"));
        var values = extra ?? new Dictionary<string, object?>();
        values[keys["狀態"]] = options[$"狀態/{statusLabel}"];
        return await c.PutAsJsonAsync($"{Base}/records/{Id(rec)}", new { values, rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    private async Task<JsonElement> Move(HttpClient c, JsonElement rec, string module, string statusLabel, Dictionary<string, object?>? extra = null)
    {
        var res = await TryMove(c, rec, module, statusLabel, extra);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    /// <summary>已核准的報價單：單一品項「導柱」數量 10、單價 100，客戶是往來對象「宏達科技」。</summary>
    private async Task<(JsonElement Quote, long LineId)> ApprovedQuote(HttpClient sales, HttpClient manager)
    {
        var (q, _) = await Layout("quote");
        var (l, _) = await Layout("quote-line");
        var created = await Create(sales, "quote", new() { [q["客戶"]] = _customer, [q["負責業務"]] = "sales01" },
            new Dictionary<string, object[]>
            {
                [Key("quote-line")] = [new { values = new Dictionary<string, object?> { [l["品號"]] = "GP-01", [l["品名"]] = "導柱", [l["數量"]] = 10, [l["單位"]] = "pcs", [l["單價"]] = 100 } }]
            });
        var pending = await Move(sales, created, "quote", "待核准");
        (await TryMove(sales, pending, "quote", "已核准")).StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        var approved = await Move(manager, pending, "quote", "已核准");
        var fresh = await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(approved)}"));
        return (fresh, fresh.GetProperty("details").GetProperty(Key("quote-line"))[0].GetProperty("id").GetInt64());
    }

    private string ConvertUrl(JsonElement quote) => $"{Base}/records/{Id(quote)}/conversions/mes-quote-to-order/convert";

    [Fact]
    public async Task QuoteToOrder_PartialConversions_CarryCustomerAndTextUnit_AndStopAtTheQuoteQty()
    {
        await OpenMes();
        var sales = _app.AsUser("sales01"); var manager = _app.AsUser("manager01");
        var (quote, lineId) = await ApprovedQuote(sales, manager);

        var first = await manager.PostAsJsonAsync(ConvertUrl(quote), new { lines = new[] { new { sourceLineId = lineId, quantity = 4 } } });
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var order = await JsonOf(first);
        var (o, _) = await Layout("order");
        order.GetProperty("values").GetProperty(o["客戶"]).GetInt64().Should().Be(_customer);
        order.GetProperty("values").GetProperty(o["來源報價單"]).GetInt64().Should().Be(Id(quote));
        var row = (await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(order)}"))).GetProperty("details").GetProperty(Key("line"))[0].GetProperty("values");
        var (ol, _) = await Layout("line");
        row.GetProperty(ol["單位"]).GetString().Should().Be("pcs");
        row.GetProperty(ol["數量"]).GetDecimal().Should().Be(4m);

        (await manager.PostAsJsonAsync(ConvertUrl(quote), new { lines = new[] { new { sourceLineId = lineId, quantity = 6 } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await manager.PostAsJsonAsync(ConvertUrl(quote), new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuoteToOrder_ConcurrentRequests_NeverConvertMoreThanTheQuoteQty()
    {
        await OpenMes();
        var sales = _app.AsUser("sales01"); var manager = _app.AsUser("manager01");
        var (quote, lineId) = await ApprovedQuote(sales, manager);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            manager.PostAsJsonAsync(ConvertUrl(quote), new { lines = new[] { new { sourceLineId = lineId, quantity = 5 } } })));

        var created = results.Count(r => r.StatusCode == HttpStatusCode.Created);
        created.Should().BeInRange(1, 2, "數量 10、每次 5，最多成功兩次");
        results.Where(r => r.StatusCode != HttpStatusCode.Created).Should().OnlyContain(r => (int)r.StatusCode >= 400 && (int)r.StatusCode < 500);
        var orders = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Key("order")}/records/paged?currentPage=1&pagesSize=50"));
        orders.GetProperty("totalCount").GetInt32().Should().Be(created);
    }

    [Fact]
    public async Task WorkOrder_TakesTheOrderDueDateAsDefault_AndKeepsAnEditedOne()
    {
        await OpenMes();
        var manager = _app.AsUser("manager01"); var planner = _app.AsUser("planner01");
        var (o, _) = await Layout("order"); var (w, _) = await Layout("workorder");
        var order = await Create(manager, "order", new() { [o["客戶"]] = _customer, [o["交期"]] = "2026-12-20" });

        var wo = await Create(planner, "workorder", new() { [w["品名"]] = "導柱", [w["工令數量"]] = 5, [w["訂單"]] = Id(order) });
        wo.GetProperty("values").GetProperty(w["交期"]).GetString().Should().Be("2026-12-20");
        var custom = await Create(planner, "workorder", new() { [w["品名"]] = "頂針", [w["工令數量"]] = 5, [w["訂單"]] = Id(order), [w["交期"]] = "2026-12-25" });
        custom.GetProperty("values").GetProperty(w["交期"]).GetString().Should().Be("2026-12-25");
    }

    [Fact]
    public async Task Permissions_PlannerRunsProduction_FloorOnlyReportsAndReverses_SalesCannotTouchWorkOrders()
    {
        await OpenMes();
        var planner = _app.AsUser("planner01"); var floor = _app.AsUser("floor01"); var sales = _app.AsUser("sales01");
        var (w, _) = await Layout("workorder"); var (r, ro) = await Layout("report");

        (await TryCreate(floor, "workorder", new() { [w["品名"]] = "x", [w["工令數量"]] = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TryCreate(sales, "workorder", new() { [w["品名"]] = "x", [w["工令數量"]] = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var draft = await Create(planner, "workorder", new() { [w["品名"]] = "導柱", [w["工令數量"]] = 50, [w["末道工序序號"]] = 2 });
        (await TryMove(floor, draft, "workorder", "已發放")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        var released = await Move(planner, draft, "workorder", "已發放");

        var rep = await Create(floor, "report", new()
        {
            [r["工令"]] = Id(released), [r["工序序號"]] = 2, [r["工序"]] = ro["工序/精加工"], [r["良品數"]] = 20, [r["不良數"]] = 0
        });
        (await floor.PostAsJsonAsync($"{Base}/records/{Id(rep)}/reverse", new { })).IsSuccessStatusCode.Should().BeTrue("現場有新增權限，可以沖銷");
        (await _app.AsUser("planner01").GetAsync($"{Base}/records/{Id(released)}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Order_StatusMachine_PlannerCannotAdvance_ClosedOrdersAreLocked()
    {
        await OpenMes();
        var manager = _app.AsUser("manager01"); var planner = _app.AsUser("planner01");
        var (o, _) = await Layout("order");
        var order = await Create(manager, "order", new() { [o["客戶"]] = _customer });
        var confirmed = await Move(manager, order, "order", "已確認");
        (await TryMove(planner, confirmed, "order", "出貨中")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        var shipping = await Move(manager, confirmed, "order", "出貨中");
        var closed = await Move(manager, shipping, "order", "已結案");
        var edit = await manager.PutAsJsonAsync($"{Base}/records/{Id(closed)}", new
        {
            values = new Dictionary<string, object?> { [o["交期"]] = "2027-01-01" }, rowVersion = closed.GetProperty("rowVersion").GetString()
        });
        edit.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
        var now = await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(closed)}"));
        var hasDue = now.GetProperty("values").TryGetProperty(o["交期"], out var due);
        (!hasDue || due.ValueKind == JsonValueKind.Null).Should().BeTrue("唯讀狀態：表頭欄位的變更不生效（審查第二輪 #7）");
    }

    [Fact]
    public async Task WorkOrderSheet_PrintsTheOperationsAndEscapesValues()
    {
        await OpenMes();
        var planner = _app.AsUser("planner01");
        var (w, _) = await Layout("workorder"); var (p, po) = await Layout("wo-op");
        var wo = await Create(planner, "workorder", new() { [w["品名"]] = "<script>alert(1)</script>導柱", [w["工令數量"]] = 5 },
            new Dictionary<string, object[]>
            {
                [Key("wo-op")] =
                [
                    new { values = new Dictionary<string, object?> { [p["工序"]] = po["工序/放電"], [p["工作站"]] = "EDM-3", [p["預計工時"]] = 6.5 } },
                    new { values = new Dictionary<string, object?> { [p["工序"]] = po["工序/拋光"], [p["工作站"]] = "PL-1" } }
                ]
            });
        var res = await planner.GetAsync($"{Base}/records/{Id(wo)}/print/mes-wo-sheet?locale=zh-TW");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("放電").And.Contain("EDM-3").And.Contain("拋光").And.Contain("PL-1").And.Contain("6.5");
        html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
        (await _app.AsUser("sales01").GetAsync($"{Base}/records/{Id(wo)}/print/mes-wo-sheet?locale=zh-TW")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OverdueWorkOrderReport_ListsOnlyUnfinishedWorkOrdersPastTheirDueDate()
    {
        await OpenMes();
        var planner = _app.AsUser("planner01");
        var (w, _) = await Layout("workorder");
        async Task<JsonElement> Wo(string name, string due, bool release)
        {
            var draft = await Create(planner, "workorder", new() { [w["品名"]] = name, [w["工令數量"]] = 5, [w["交期"]] = due, [w["末道工序序號"]] = 1 });
            return release ? await Move(planner, draft, "workorder", "已發放") : draft;
        }
        await Wo("逾期導柱", "2026-01-05", release: true);
        await Wo("未到期頂針", "2099-01-05", release: true);
        await Wo("逾期但還是草稿", "2026-01-06", release: false);

        var list = await JsonOf(await planner.GetAsync($"{Base}/reports"));
        var report = list.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "逾期未完工工令");
        var run = await planner.PostAsJsonAsync($"{Base}/reports/{report.GetProperty("id").GetInt64()}/run", new { range = (object?)null });
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        var body = await run.Content.ReadAsStringAsync();
        body.Should().Contain("逾期導柱").And.NotContain("未到期頂針").And.NotContain("逾期但還是草稿");
    }

    [Fact]
    public async Task ShipmentSheet_Prints()
    {
        await OpenMes();
        var planner = _app.AsUser("planner01");
        var (w, _) = await Layout("workorder"); var (r, ro) = await Layout("report"); var (s, _) = await Layout("shipment");
        var draft = await Create(planner, "workorder", new() { [w["品名"]] = "導柱", [w["工令數量"]] = 10, [w["末道工序序號"]] = 1 });
        var wo = await Move(planner, draft, "workorder", "已發放");
        await Create(planner, "report", new() { [r["工令"]] = Id(wo), [r["工序序號"]] = 1, [r["工序"]] = ro["工序/備料"], [r["良品數"]] = 10 });
        await Move(planner, wo, "workorder", "完工");
        var ship = await Create(planner, "shipment", new() { [s["工令"]] = Id(wo), [s["客戶"]] = _customer, [s["出貨數量"]] = 10, [s["運送方式"]] = "貨運" });
        var html = await (await planner.GetAsync($"{Base}/records/{Id(ship)}/print/mes-ship-sheet?locale=zh-TW")).Content.ReadAsStringAsync();
        html.Should().Contain("出貨單").And.Contain("貨運").And.Contain("10");
    }

    [Fact]
    public async Task MesAlone_MoldIsItsOwnModule_RepairAndCarePickMoldRecords()
    {
        // s2d-0：模具歸 MES 自己的模組 mes-mold，報修與保養的模具參照指向它
        await OpenMes();
        Key("mold").Should().Be("mes-mold");
        var maint = _app.AsUser("maint01"); var floor = _app.AsUser("floor01");
        var (m, _) = await Layout("mold");
        var mold = await Create(maint, "mold", new() { [m["模具編號"]] = "MD-01", [m["名稱"]] = "測試上蓋模", [m["保養週期（天）"]] = 30 });
        (await TryCreate(maint, "mold", new() { [m["名稱"]] = "缺編號", [m["保養週期（天）"]] = 30 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (var (module, user) in new[] { ("repair", floor), ("care", maint) })
        {
            var schema = await JsonOf(await user.GetAsync($"{Base}/modules/{Key(module)}/schema"));
            var reference = schema.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
                .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "模具").GetProperty("reference");
            reference.GetProperty("kind").GetString().Should().Be("module");
            reference.GetProperty("key").GetString().Should().Be("mes-mold");
            // 標題樣板 {no} {name}：用編號或名稱都找得到
            foreach (var keyword in new[] { "上蓋", "MD-01" })
            {
                var light = await JsonOf(await user.GetAsync($"{Base}{reference.GetProperty("lightUrl").GetString()}?keyword={keyword}"));
                light.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).Should().Equal(Id(mold).ToString());
                light.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("MD-01 測試上蓋模");
            }
        }

        var (r, _) = await Layout("repair");
        var repair = await Create(floor, "repair", new() { [r["模具"]] = Id(mold), [r["申請人"]] = "floor01", [r["故障說明"]] = "冷卻水路漏水" });
        var info = (await JsonOf(await floor.GetAsync($"{Base}/records/{Id(repair)}"))).GetProperty("references").GetProperty(r["模具"]);
        info.GetProperty("title").GetString().Should().Be("MD-01 測試上蓋模");
        var (c, _) = await Layout("care");
        await Create(maint, "care", new() { [c["模具"]] = Id(mold), [c["保養日期"]] = "2026-11-01", [c["保養週期（天）"]] = 30 });

        // 被報修參照的模具不能刪（參照預設 restrict）
        var current = await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(mold)}"));
        (await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Base}/records/{Id(mold)}?rowVersion={Uri.EscapeDataString(current.GetProperty("rowVersion").GetString()!)}")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Open_WhenACustomerExtensionModuleAlreadyExists_StillWorks_BecauseMesNoLongerExtendsTheCoreCustomer()
    {
        // s2d-1f：MES 的客戶改用共用主檔往來對象，不再擴充核心實體 sales.customer，自建的客戶擴充不再擋下開啟
        var res = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key = "my-customer-ext", kind = "extension", hostEntityKey = "sales.customer", name = new Dictionary<string, string> { ["zh-TW"] = "自建客戶擴充" }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());

        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/mes-mold/open", new { groupMap = new Dictionary<string, object>() });
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        (await _admin.GetAsync($"{Base}/modules/mes-customer-info")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
