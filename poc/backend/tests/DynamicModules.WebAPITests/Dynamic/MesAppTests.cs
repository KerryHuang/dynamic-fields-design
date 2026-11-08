using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Mock;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建應用程式「MES 模具廠」（apps/mes-mold.app.json）的工令、報工、出貨流程。
/// 程式碼裡沒有任何 MES 專用邏輯，這些行為全來自套件宣告（狀態機、規則、彙總、事件、檢查、分錄）。
/// 同時是設計 §8 可行性假設的驗證。
/// </summary>
public class MesAppTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new(seedDemoPeople: false);
    private readonly HttpClient _admin;
    private readonly Dictionary<string, (Dictionary<string, string> Keys, Dictionary<string, string> Options)> _layouts = new();
    private JsonElement _opened;

    public MesAppTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    // ---- helpers ----

    private async Task OpenMes()
    {
        var body = new
        {
            groupMap = new[] { "sales", "salesManager", "floor", "maint", "qa", "planner" }
                .ToDictionary(g => g, _ => (object)new { mode = "create" })
        };
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/mes-mold/open", body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        _opened = await JsonOf(res);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<MockCustomer>().Add(new MockCustomer { CustomerNo = "C-001", Name = "宏達科技" });
        await db.SaveChangesAsync();
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

    private async Task<HttpResponseMessage> TryCreate(string module, Dictionary<string, object?> values) =>
        await _admin.PostAsJsonAsync($"{Base}/modules/{Key(module)}/records", new { values });

    private async Task<JsonElement> Create(string module, Dictionary<string, object?> values)
    {
        var res = await TryCreate(module, values);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<JsonElement> Get(JsonElement rec) => await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(rec)}"));

    private async Task<HttpResponseMessage> TryMove(JsonElement rec, string module, string statusLabel, Dictionary<string, object?>? extra = null)
    {
        var (keys, options) = await Layout(module);
        var current = await Get(rec);
        var values = extra ?? new Dictionary<string, object?>();
        values[keys["狀態"]] = options[$"狀態/{statusLabel}"];
        return await _admin.PutAsJsonAsync($"{Base}/records/{Id(rec)}", new { values, rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    private async Task<JsonElement> Move(JsonElement rec, string module, string statusLabel, Dictionary<string, object?>? extra = null)
    {
        var res = await TryMove(rec, module, statusLabel, extra);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<decimal?> Num(JsonElement rec, string module, string label)
    {
        var v = (await Get(rec)).GetProperty("values").TryGetProperty((await Layout(module)).Keys[label], out var e) ? e : default;
        return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    }

    private async Task<string> StatusOf(JsonElement rec, string module)
    {
        var (keys, options) = await Layout(module);
        var value = (await Get(rec)).GetProperty("values").GetProperty(keys["狀態"]).GetString();
        return options.Single(o => o.Key.StartsWith("狀態/") && o.Value == value).Key["狀態/".Length..];
    }

    /// <summary>已發放的工令（末道工序序號 = 3）。</summary>
    private async Task<JsonElement> ReleasedWorkOrder(int qty = 100, int finalSeq = 3)
    {
        var (w, _) = await Layout("workorder");
        var draft = await Create("workorder", new() { [w["品名"]] = "導柱", [w["工令數量"]] = qty, [w["末道工序序號"]] = finalSeq });
        return await Move(draft, "workorder", "已發放");
    }

    private async Task<HttpResponseMessage> TryReport(JsonElement wo, int opSeq, int good, int defect = 0, string? defectReason = null)
    {
        var (r, ro) = await Layout("report");
        var values = new Dictionary<string, object?>
        {
            [r["工令"]] = Id(wo), [r["工序序號"]] = opSeq, [r["工序"]] = ro["工序/備料"], [r["良品數"]] = good, [r["不良數"]] = defect
        };
        if (defectReason is not null) values[r["不良原因"]] = ro[$"不良原因/{defectReason}"];
        return await TryCreate("report", values);
    }

    private async Task<JsonElement> Report(JsonElement wo, int opSeq, int good, int defect = 0, string? defectReason = null)
    {
        var res = await TryReport(wo, opSeq, good, defect, defectReason);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<HttpResponseMessage> TryShipment(JsonElement wo, int qty)
    {
        var (s, _) = await Layout("shipment");
        return await TryCreate("shipment", new() { [s["工令"]] = Id(wo), [s["客戶"]] = "C-001", [s["出貨數量"]] = qty });
    }

    private async Task<JsonElement> Shipment(JsonElement wo, int qty)
    {
        var res = await TryShipment(wo, qty);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<JsonElement> DoneWorkOrder(int qty = 100)
    {
        var wo = await ReleasedWorkOrder(qty);
        await Report(wo, 3, qty);
        return await Move(wo, "workorder", "完工");
    }

    private static async Task Body(HttpResponseMessage res, string contains) =>
        (await res.Content.ReadAsStringAsync()).Should().Contain(contains);

    // ---- 開啟 ----

    [Fact]
    public async Task OpenFromEmptySystem_ModulesAreKeyedWithMesPrefix()
    {
        (await _admin.GetAsync($"{Base}/modules/mes-workorder")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await OpenMes();
        var keys = _opened.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()!).ToList();
        keys.Should().HaveCount(14).And.OnlyContain(k => k.StartsWith("mes-"));
        keys.Should().Contain(["mes-quote", "mes-order", "mes-workorder", "mes-wo-op", "mes-report", "mes-shipment", "mes-quality"]);
    }

    // ---- 報工：累計上限、末道、完工量 ----

    [Fact]
    public async Task Report_FirstReport_AdvancesReleasedWorkOrderToRunning_AndStampsTheDate()
    {
        await OpenMes();
        var wo = await ReleasedWorkOrder();
        await Report(wo, 1, 10);
        (await StatusOf(wo, "workorder")).Should().Be("生產中");
        (await Get(wo)).GetProperty("values").GetProperty((await Layout("workorder")).Keys["最後報工日"]).ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Report_CumulativeGoodQty_PerOperation_CannotExceedWorkOrderQty()
    {
        await OpenMes();
        var wo = await ReleasedWorkOrder(100);
        await Report(wo, 1, 60);
        await Report(wo, 1, 40);
        var over = await TryReport(wo, 1, 1);
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Body(over, "此工序累計良品數超過工令數量");
        await Report(wo, 2, 100);   // 別的工序各自累計
    }

    [Fact]
    public async Task Report_OnlyTheFinalOperation_CountsTowardsCompletedQty()
    {
        await OpenMes();
        var wo = await ReleasedWorkOrder(100, finalSeq: 3);
        await Report(wo, 1, 100);
        await Report(wo, 2, 100);
        (await Num(wo, "workorder", "已完工量")).Should().Be(0);
        await Report(wo, 3, 60);
        await Report(wo, 3, 40);
        (await Num(wo, "workorder", "已完工量")).Should().Be(100);
        (await Num(wo, "workorder", "剩餘量")).Should().Be(0);
    }

    [Fact]
    public async Task Report_DefectsDoNotUseUpTheGoodQtyAllowance_AndNeedAReason()
    {
        await OpenMes();
        var wo = await ReleasedWorkOrder(100);
        var noReason = await TryReport(wo, 3, 100, defect: 1);
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Report(wo, 3, 100, defect: 1, defectReason: "刮傷");
        await Report(wo, 3, 0, defect: 1, defectReason: "毛邊");   // 補做不良件不被擋
        (await Num(wo, "workorder", "不良總數")).Should().Be(2);
        await Move(wo, "workorder", "完工");
    }

    [Fact]
    public async Task WorkOrder_FinalSeq_IsRequiredWhenReleased_AndLockedAfterwards()
    {
        await OpenMes();
        var (w, _) = await Layout("workorder");
        var draft = await Create("workorder", new() { [w["品名"]] = "頂針板", [w["工令數量"]] = 10 });
        (await TryMove(draft, "workorder", "已發放")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var released = await Move(draft, "workorder", "已發放", new() { [w["末道工序序號"]] = 4 });
        var change = await _admin.PutAsJsonAsync($"{Base}/records/{Id(released)}", new
        {
            values = new Dictionary<string, object?> { [w["末道工序序號"]] = 5 },
            rowVersion = (await Get(released)).GetProperty("rowVersion").GetString()
        });
        change.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        (await Num(released, "workorder", "末道工序序號")).Should().Be(4);   // 唯讀規則：變更不生效
    }

    // ---- 完工、短交、暫停 ----

    [Fact]
    public async Task WorkOrder_Done_NeedsCompletedQty_OrAShortClose()
    {
        await OpenMes();
        var (w, _) = await Layout("workorder");
        var wo = await ReleasedWorkOrder(100);
        await Report(wo, 3, 60);
        var short_ = await TryMove(wo, "workorder", "完工");
        short_.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Body(short_, "已完工量不足");
        await Move(wo, "workorder", "完工", new() { [w["短交結案"]] = true });
        (await StatusOf(wo, "workorder")).Should().Be("完工");
    }

    [Fact]
    public async Task WorkOrder_OnHold_RejectsReports_UntilReleasedAgain()
    {
        await OpenMes();
        var (w, _) = await Layout("workorder");
        var wo = await ReleasedWorkOrder();
        var held = await Move(wo, "workorder", "暫停", new() { [w["暫停原因"]] = (await Layout("workorder")).Options["暫停原因/缺料"] });
        var blocked = await TryReport(held, 1, 5);
        blocked.StatusCode.Should().NotBe(HttpStatusCode.Created);
        await Body(blocked, "mes-report-progress");
        await Move(held, "workorder", "已發放");
        await Report(held, 1, 5);
    }

    [Fact]
    public async Task WorkOrder_Done_RejectsNewReports()
    {
        await OpenMes();
        var wo = await DoneWorkOrder();
        var late = await TryReport(wo, 1, 1);
        late.StatusCode.Should().NotBe(HttpStatusCode.Created);
        await Body(late, "唯讀");
    }

    // ---- 分錄：報工只能沖銷 ----

    [Fact]
    public async Task Report_IsALedger_NoEditNoDelete_ReverseNegatesAndReduces()
    {
        await OpenMes();
        var wo = await ReleasedWorkOrder(100);
        var rep = await Report(wo, 3, 80, defect: 4, defectReason: "尺寸");
        var (r, _) = await Layout("report");

        (await _admin.PutAsJsonAsync($"{Base}/records/{Id(rep)}", new
        {
            values = new Dictionary<string, object?> { [r["良品數"]] = 1 }, rowVersion = rep.GetProperty("rowVersion").GetString()
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"{Base}/records/{Id(rep)}")).IsSuccessStatusCode.Should().BeFalse();

        (await Num(wo, "workorder", "已完工量")).Should().Be(80);
        (await Num(wo, "workorder", "不良總數")).Should().Be(4);
        var reversed = await _admin.PostAsJsonAsync($"{Base}/records/{Id(rep)}/reverse", new { });
        reversed.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        (await Num(wo, "workorder", "已完工量")).Should().Be(0);
        (await Num(wo, "workorder", "不良總數")).Should().Be(0);
        (await _admin.PostAsJsonAsync($"{Base}/records/{Id(rep)}/reverse", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await Report(wo, 3, 100);   // 沖銷後同工序可再報
    }

    [Fact]
    public async Task Report_Reverse_IsBlockedWhileWorkOrderIsDone_UntilReopened()
    {
        await OpenMes();
        var wo = await DoneWorkOrder();
        var reports = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Key("report")}/records/paged?currentPage=1&pagesSize=10"));
        var id = reports.GetProperty("items")[0].GetProperty("id").GetInt64();
        (await _admin.PostAsJsonAsync($"{Base}/records/{id}/reverse", new { })).StatusCode.Should().NotBe(HttpStatusCode.OK).And.NotBe(HttpStatusCode.Created);
        await Move(wo, "workorder", "生產中");
        (await _admin.PostAsJsonAsync($"{Base}/records/{id}/reverse", new { })).IsSuccessStatusCode.Should().BeTrue();
        (await Num(wo, "workorder", "已完工量")).Should().Be(0);
    }

    // ---- 出貨：只限完工的工令（審查第二輪） ----

    [Fact]
    public async Task Shipment_OnlyForFinishedWorkOrders()
    {
        await OpenMes();
        var released = await ReleasedWorkOrder(100);
        var notStarted = await TryShipment(released, 10);
        notStarted.StatusCode.Should().NotBe(HttpStatusCode.Created);
        await Body(notStarted, "mes-shipment-close-wo");

        await Report(released, 3, 60);   // 生產中，完工量不足
        var partial = await TryShipment(released, 10);
        partial.StatusCode.Should().NotBe(HttpStatusCode.Created);
        (await StatusOf(released, "workorder")).Should().Be("生產中");

        var done = await DoneWorkOrder(50);
        var ok = await Shipment(done, 50);
        (await Num(ok, "shipment", "工令完工量")).Should().Be(50);
    }

    [Fact]
    public async Task Shipment_CannotShipMoreThanCompleted_DraftsAreFree_CancelFreesTheQty()
    {
        await OpenMes();
        var wo = await DoneWorkOrder(100);
        var a = await Shipment(wo, 60);
        var b = await Shipment(wo, 40);
        var c = await Shipment(wo, 10);   // 草稿不占用，存得進去
        await Move(a, "shipment", "已出貨");
        await Move(b, "shipment", "已出貨");
        var over = await TryMove(c, "shipment", "已出貨");
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Body(over, "出貨數量超過工令已完工量");
        (await Num(wo, "workorder", "已出貨量")).Should().Be(100);

        await Move(a, "shipment", "已取消");   // 退貨：加總 40 ≤ 100，不被擋（審查第二輪 #1）
        (await Num(wo, "workorder", "已出貨量")).Should().Be(40);
        await Move(c, "shipment", "已出貨");
        (await Num(wo, "workorder", "已出貨量")).Should().Be(50);
    }

    [Fact]
    public async Task WorkOrder_Reopen_IsBlockedWhileThereAreOpenShipments_AndAllowedAfterCancelling()
    {
        await OpenMes();
        var wo = await DoneWorkOrder(100);
        var ship = await Shipment(wo, 100);
        var draftBlocks = await TryMove(wo, "workorder", "生產中");
        draftBlocks.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Body(draftBlocks, "請先取消出貨單");

        await Move(ship, "shipment", "已出貨");
        (await TryMove(wo, "workorder", "生產中")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await Move(ship, "shipment", "已取消");
        await Move(wo, "workorder", "生產中");
        (await StatusOf(wo, "workorder")).Should().Be("生產中");
    }

    [Fact]
    public async Task WorkOrder_ShortClosed_CanBeShippedUpToWhatWasCompleted()
    {
        await OpenMes();
        var (w, _) = await Layout("workorder");
        var wo = await ReleasedWorkOrder(100);
        await Report(wo, 3, 70);
        await Move(wo, "workorder", "完工", new() { [w["短交結案"]] = true });
        var s = await Shipment(wo, 70);
        await Move(s, "shipment", "已出貨");
        var more = await Shipment(wo, 1);
        (await TryMove(more, "shipment", "已出貨")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Shipment_IsReadOnlyOnceShippedOrCancelled()
    {
        await OpenMes();
        var wo = await DoneWorkOrder(100);
        var s = await Move(await Shipment(wo, 10), "shipment", "已出貨");
        var (sk, _) = await Layout("shipment");
        var edit = await _admin.PutAsJsonAsync($"{Base}/records/{Id(s)}", new
        {
            values = new Dictionary<string, object?> { [sk["出貨數量"]] = 99 }, rowVersion = s.GetProperty("rowVersion").GetString()
        });
        edit.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.OK);
        (await Num(s, "shipment", "出貨數量")).Should().Be(10);   // 唯讀狀態：數量變更不生效
    }
}
