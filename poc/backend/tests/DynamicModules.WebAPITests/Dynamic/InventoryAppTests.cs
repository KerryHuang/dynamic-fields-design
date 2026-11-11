using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.InventoryRig;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 出貨的內建應用程式「進銷存」（apps/inventory.app.json）：從空系統開啟，走採購 → 進貨（轉換、過帳、取消沖銷）、
/// 出貨與盤點（檢查擋透支、整筆回滾）、庫存異動只能沖銷、權限、套件往返與列印。程式碼裡沒有任何進銷存專用邏輯。
/// </summary>
public class InventoryAppTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public InventoryAppTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static string ConvertUrl(JsonElement source) => $"{Base}/records/{Id(source)}/conversions/inv-po-to-receipt/convert";

    [Fact]
    public async Task NotOpened_NothingAppears_OpenFromEmpty_ClosedIsReadOnly_ReopenKeepsData()
    {
        (await JsonOf(await _admin.GetAsync(Apps))).GetArrayLength().Should().Be(0);
        (await JsonOf(await _admin.GetAsync($"{Apps}/packages"))).EnumerateArray().Select(p => p.GetProperty("id").GetString())
            .Should().Contain("inventory");
        (await _admin.GetAsync($"{Base}/modules/inv-item")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rig = await OpenAsync(_admin);
        rig.App.GetProperty("modules").GetArrayLength().Should().Be(11, "供應商、客戶改用共用主檔往來對象");
        rig.App.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().OnlyContain(k => k!.StartsWith("inv-"));
        var item = await rig.ItemAsync("螺絲 M3");

        (await _admin.PostAsJsonAsync($"{Apps}/inventory/close", new { cascade = false })).EnsureSuccessStatusCode();
        var read = await _admin.GetAsync($"{Base}/records/{item}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var f = await rig.F("item");
        Str(await JsonOf(read), f["名稱"]).Should().Be("螺絲 M3");
        (await _admin.PostAsJsonAsync($"{Base}/modules/{rig.Key("item")}/records",
            new { values = new Dictionary<string, object> { [f["名稱"]] = "x" } })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _admin.PostAsync($"{Apps}/inventory/reopen", null)).EnsureSuccessStatusCode();
        Str(await rig.GetAsync(item), f["名稱"]).Should().Be("螺絲 M3");
        await rig.ItemAsync("螺帽 M3");
    }

    [Fact]
    public async Task PurchaseOrder_Rules_ExpectedDateAndCancelRemark()
    {
        var rig = await OpenAsync(_admin);
        var supplier = await rig.SupplierAsync("鑫盛五金");
        var p = await rig.F("po");

        var early = await _admin.PostAsJsonAsync($"{Base}/modules/{rig.Key("po")}/records", new
        {
            values = new Dictionary<string, object> { [p["供應商"]] = supplier, [p["採購日期"]] = "2026-11-10", [p["預定交期"]] = "2026-11-01" }
        });
        early.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await early.Content.ReadAsStringAsync()).Should().Contain("預定交期不得早於採購日期");

        var po = await PostOk(_admin, $"{Base}/modules/{rig.Key("po")}/records",
            new { values = new Dictionary<string, object> { [p["供應商"]] = supplier } });
        (await rig.TransitionAsync(_admin, po, "po", "已取消")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "取消時備註必填");
        var cancelled = await rig.TransitionAsync(_admin, po, "po", "已取消", new() { [p["備註"]] = "供應商停產" });
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PoToReceipt_PartialConversion_PostCreatesMoves_CancelReverses()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("鋼板", price: 120, unit: "kg");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var supplier = await rig.SupplierAsync("鑫盛鋼鐵");
        var p = await rig.F("po");
        var pl = await rig.F("po-line");
        var r = await rig.F("receipt");
        var rl = await rig.F("receipt-line");
        var m = await rig.F("move");

        var buyer = _app.AsUser(Buyer);
        var draft = await PostOk(buyer, $"{Base}/modules/{rig.Key("po")}/records", new
        {
            values = new Dictionary<string, object> { [p["供應商"]] = supplier, [p["預定入庫倉"]] = wh },
            details = new Dictionary<string, object[]>
            {
                [rig.Key("po-line")] = [new { values = new Dictionary<string, object> { [pl["品項"]] = item, [pl["數量"]] = 10 } }]
            }
        });
        var poLine = draft.GetProperty("details").GetProperty(rig.Key("po-line"))[0];
        Str(poLine, pl["品名"]).Should().Be("鋼板", "選品項後品名由系統帶入");
        Num(poLine, pl["單價"]).Should().Be(120m);
        (await _admin.PostAsJsonAsync(ConvertUrl(draft), new { lines = new[] { new { sourceLineId = Id(poLine), quantity = 4 } } }))
            .StatusCode.Should().NotBe(HttpStatusCode.Created, "草稿採購單不能轉進貨");
        var po = await rig.TransitionOk(buyer, draft, "po", "已確認");

        // 倉管轉進貨：部分轉換 4 + 6，第三次超過剩餘量
        var keeper = _app.AsUser(Keeper);
        var first = await PostOk(keeper, ConvertUrl(po), new { lines = new[] { new { sourceLineId = Id(poLine), quantity = 4 } } });
        Ref(first, r["來源採購單"]).Should().Be(Id(po));
        Ref(first, r["供應商"]).Should().Be(supplier);
        Ref(first, r["入庫倉"]).Should().Be(wh);
        var firstFull = await rig.GetAsync(Id(first));
        var rLine = firstFull.GetProperty("details").GetProperty(rig.Key("receipt-line"))[0];
        Num(rLine, rl["數量"]).Should().Be(4m);
        Num(rLine, rl["單價"]).Should().Be(120m);
        Str(rLine, rl["單位"]).Should().Be("kg");
        Num(rLine, rl["來源採購明細"]).Should().Be(Id(poLine));
        await PostOk(keeper, ConvertUrl(po), new { lines = new[] { new { sourceLineId = Id(poLine), quantity = 6 } } });
        (await keeper.PostAsJsonAsync(ConvertUrl(po), new { lines = new[] { new { sourceLineId = Id(poLine), quantity = 1 } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var status = await JsonOf(await _admin.GetAsync($"{Base}/records/{Id(po)}/conversions"));
        status.EnumerateArray().Single(c => c.GetProperty("key").GetString() == "inv-po-to-receipt")
            .GetProperty("lines")[0].GetProperty("remaining").GetDecimal().Should().Be(0m);

        // 倉管過帳：產生一筆入庫異動，數量、倉庫、單號、日期正確，擁有者是單據擁有者（倉管）
        var posted = await rig.TransitionOk(keeper, firstFull, "receipt", "已過帳");
        var moves = await rig.MovesAsync();
        moves.Should().HaveCount(1);
        var mv = moves[0];
        Ref(mv, m["品項"]).Should().Be(item);
        Ref(mv, m["倉庫"]).Should().Be(wh);
        Num(mv, m["數量（入正出負）"]).Should().Be(4m);
        Str(mv, m["類型"]).Should().Be(await rig.Opt("move", "類型", "入庫"));
        Str(mv, m["來源單號"]).Should().Be(Str(firstFull, r["進貨單號"])).And.StartWith("RC-");
        Str(mv, m["異動日期"]).Should().Be(DateTime.Today.ToString("yyyy-MM-dd"));
        mv.GetProperty("ownerUserId").GetString().Should().Be(firstFull.GetProperty("ownerUserId").GetString()).And.Be(Keeper);
        (await rig.BalanceAsync(item, wh)).Should().Be(4m);

        // 過帳後唯讀：改備註不生效或被擋，刪除被擋
        var edit = await keeper.PutAsJsonAsync($"{Base}/records/{Id(posted)}", new
        {
            values = new Dictionary<string, object> { [r["備註"]] = "過帳後想改" }, rowVersion = posted.GetProperty("rowVersion").GetString()
        });
        if (edit.IsSuccessStatusCode) Str(await JsonOf(edit), r["備註"]).Should().BeNull();
        (await _admin.DeleteAsync($"{Base}/records/{Id(posted)}?rowVersion={Uri.EscapeDataString(posted.GetProperty("rowVersion").GetString()!)}"))
            .IsSuccessStatusCode.Should().BeFalse();

        // 取消已過帳只有主管能做；取消後事件沖銷，餘額回到 0
        var current = await rig.GetAsync(Id(posted));
        (await rig.TransitionAsync(keeper, current, "receipt", "已取消")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var cancelled = await rig.TransitionAsync(_app.AsUser(Manager), current, "receipt", "已取消");
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
        moves = await rig.MovesAsync();
        moves.Should().HaveCount(2);
        Ref(moves[1], m["沖銷的異動"]).Should().Be(Id(mv));
        Num(moves[1], m["數量（入正出負）"]).Should().Be(-4m);
        (await rig.BalanceAsync(item, wh)).Should().Be(0m);
    }

    [Fact]
    public async Task Shipment_DeductsStock_InsufficientIs400_AndTheWholeRequestRollsBack()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("軸承");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var other = await rig.WarehouseAsync("B", "備品倉");
        var customer = await rig.CustomerAsync("立新機械");
        await rig.OpeningAsync(wh, item, 10);

        var s1 = await rig.ShipmentDraftAsync(customer, wh, item, 3);
        await rig.TransitionOk(_admin, s1, "shipment", "已出貨");
        (await rig.BalanceAsync(item, wh)).Should().Be(7m);
        var moveCount = (await rig.MovesAsync()).Count;

        var s2 = await rig.ShipmentDraftAsync(customer, wh, item, 8);
        var over = await rig.TransitionAsync(_admin, s2, "shipment", "已出貨");
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(over);
        body.GetProperty("errors").GetProperty("_record").EnumerateArray().Select(e => e.GetString()).Should().Contain("庫存不足，不能出庫");
        var after = await rig.GetAsync(Id(s2));
        Str(after, (await rig.F("shipment"))["狀態"]).Should().Be(await rig.Opt("shipment", "狀態", "草稿"));
        (await rig.MovesAsync()).Should().HaveCount(moveCount);

        // 另一個倉庫沒有庫存：同品項不同倉庫各自計算
        var s3 = await rig.ShipmentDraftAsync(customer, other, item, 1);
        (await rig.TransitionAsync(_admin, s3, "shipment", "已出貨")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 取消已出貨的單據：庫存回補
        await rig.TransitionOk(_app.AsUser(Manager), await rig.GetAsync(Id(s1)), "shipment", "已取消");
        (await rig.BalanceAsync(item, wh)).Should().Be(10m);
    }

    [Fact]
    public async Task StockLossBeyondTheBalance_IsBlocked()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("墊片");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        await rig.OpeningAsync(wh, item, 5);

        var zero = await rig.Admin.PostAsJsonAsync($"{Base}/modules/{rig.Key("adjust")}/records", new
        {
            values = new Dictionary<string, object> { [(await rig.F("adjust"))["倉庫"]] = wh },
            details = new Dictionary<string, object[]>
            {
                [rig.Key("adjust-line")] = [new { values = new Dictionary<string, object> { [(await rig.F("adjust-line"))["品項"]] = item, [(await rig.F("adjust-line"))["調整數量"]] = 0 } }]
            }
        });
        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest, "調整數量不可為 0");

        var loss = await rig.AdjustDraftAsync(wh, item, -6, "損耗");
        var blocked = await rig.TransitionAsync(_admin, loss, "adjust", "已過帳");
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("庫存不足");
        (await rig.BalanceAsync(item, wh)).Should().Be(5m);

        await rig.TransitionOk(_admin, await rig.AdjustDraftAsync(wh, item, -5, "損耗"), "adjust", "已過帳");
        (await rig.BalanceAsync(item, wh)).Should().Be(0m);
    }

    [Fact]
    public async Task CancellingAReceiptWhoseStockWasAlreadyShipped_IsBlocked()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("馬達");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var supplier = await rig.SupplierAsync("大成電機");
        var customer = await rig.CustomerAsync("宏遠機械");

        var receipt = await rig.TransitionOk(_admin, await rig.ReceiptDraftAsync(supplier, wh, item, 5), "receipt", "已過帳");
        await rig.TransitionOk(_admin, await rig.ShipmentDraftAsync(customer, wh, item, 4), "shipment", "已出貨");

        var cancel = await rig.TransitionAsync(_app.AsUser(Manager), receipt, "receipt", "已取消");
        cancel.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await cancel.Content.ReadAsStringAsync()).Should().Contain("庫存不足");
        Str(await rig.GetAsync(Id(receipt)), (await rig.F("receipt"))["狀態"]).Should().Be(await rig.Opt("receipt", "狀態", "已過帳"));

        // 系統管理員直接沖銷那筆入庫異動也一樣被檢查擋下
        var m = await rig.F("move");
        var inMove = (await rig.MovesAsync()).First(x => Num(x, m["數量（入正出負）"]) == 5m);
        var reverse = await _admin.PostAsJsonAsync($"{Base}/records/{Id(inMove)}/reverse", new { values = new Dictionary<string, object>() });
        reverse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rig.BalanceAsync(item, wh)).Should().Be(1m);
    }

    [Fact]
    public async Task Moves_CannotBeEditedOrDeleted_ReversedOnlyOnce_AndAReversalCannotBeReversed()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("齒輪");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        await rig.OpeningAsync(wh, item, 10);
        var m = await rig.F("move");
        var move = (await rig.MovesAsync()).Single();

        var put = await _admin.PutAsJsonAsync($"{Base}/records/{Id(move)}", new
        {
            values = new Dictionary<string, object> { [m["數量（入正出負）"]] = 99 }, rowVersion = move.GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"{Base}/records/{Id(move)}?rowVersion={Uri.EscapeDataString(move.GetProperty("rowVersion").GetString()!)}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var reversal = await PostOk(_admin, $"{Base}/records/{Id(move)}/reverse", new { values = new Dictionary<string, object>() });
        Num(reversal, m["數量（入正出負）"]).Should().Be(-10m);
        Ref(reversal, m["沖銷的異動"]).Should().Be(Id(move));
        (await _admin.PostAsJsonAsync($"{Base}/records/{Id(move)}/reverse", new { values = new Dictionary<string, object>() }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PostAsJsonAsync($"{Base}/records/{Id(reversal)}/reverse", new { values = new Dictionary<string, object>() }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NoBusinessGroup_CanAddOrReverseMoves_ButAllCanRead()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("彈簧");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        await rig.OpeningAsync(wh, item, 10);
        var m = await rig.F("move");
        var move = (await rig.MovesAsync()).Single();

        foreach (var user in new[] { Buyer, Keeper, Manager })
        {
            var c = _app.AsUser(user);
            (await c.GetAsync($"{Base}/modules/{rig.Key("move")}/records/paged")).StatusCode.Should().Be(HttpStatusCode.OK, user);
            (await c.PostAsJsonAsync($"{Base}/modules/{rig.Key("move")}/records", new
            {
                values = new Dictionary<string, object> { [m["品項"]] = item, [m["倉庫"]] = wh, [m["數量（入正出負）"]] = 5, [m["類型"]] = await rig.Opt("move", "類型", "入庫") }
            })).StatusCode.Should().Be(HttpStatusCode.Forbidden, user);
            (await c.PostAsJsonAsync($"{Base}/records/{Id(move)}/reverse", new { values = new Dictionary<string, object>() }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, user);
        }
        (await rig.MovesAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task KeeperCreatedShipmentLines_CarryThePrice_SoTheReportAndPrintHaveNumbers()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("控制器", price: 250);
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var customer = await rig.CustomerAsync("永豐機電");
        await rig.OpeningAsync(wh, item, 10);
        var keeper = _app.AsUser(Keeper);
        var l = await rig.F("shipment-line");

        var draft = await rig.ShipmentDraftAsync(customer, wh, item, 2, keeper);
        var line = draft.GetProperty("details").GetProperty(rig.Key("shipment-line"))[0];
        Num(line, l["單價"]).Should().Be(250m, "倉管有金額權限，帶值帶得到單價");
        Num(line, l["金額"]).Should().Be(500m);
        await rig.TransitionOk(keeper, draft, "shipment", "已出貨");
        await rig.ShipmentDraftAsync(customer, wh, item, 3, keeper);   // 草稿不計入出貨金額

        var reports = await JsonOf(await keeper.GetAsync($"{Base}/reports"));
        var report = reports.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "各客戶出貨金額（未稅）");
        var run = await PostOk(keeper, $"{Base}/reports/{Id(report)}/run", new { }, HttpStatusCode.OK);
        run.GetProperty("total").GetProperty("amount").GetDecimal().Should().Be(500m);

        var html = await (await keeper.GetAsync($"{Base}/records/{Id(draft)}/print/inv-ship-sheet?locale=zh-TW")).Content.ReadAsStringAsync();
        html.Should().Contain(">250<").And.Contain(">500<");
    }

    [Fact]
    public async Task AllFiveReports_AreSharedWithTheThreeGroups_AndRun()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("軸");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        await rig.OpeningAsync(wh, item, 7);
        var manager = _app.AsUser(Manager);
        var names = new[] { "庫存餘額（品項 × 倉庫）", "月別進出量", "採購單狀態分布", "各供應商採購金額（未稅）", "各客戶出貨金額（未稅）" };
        var reports = (await JsonOf(await manager.GetAsync($"{Base}/reports"))).EnumerateArray().ToList();
        reports.Select(x => x.GetProperty("name").GetString()).Should().Contain(names);
        foreach (var r in reports.Where(x => names.Contains(x.GetProperty("name").GetString())))
            (await manager.PostAsJsonAsync($"{Base}/reports/{Id(r)}/run", new { })).StatusCode.Should().Be(HttpStatusCode.OK, r.GetProperty("name").GetString());
        var balance = reports.Single(x => x.GetProperty("name").GetString() == names[0]);
        (await PostOk(manager, $"{Base}/reports/{Id(balance)}/run", new { }, HttpStatusCode.OK)).GetProperty("total").GetProperty("qty").GetDecimal().Should().Be(7m);
    }

    [Fact]
    public async Task ExportThenImportIntoAnEmptyDatabase_EventsChecksLedgerConversionAndPrintSurvive()
    {
        await OpenAsync(_admin, assignMembers: false);
        var res = await _admin.GetAsync($"{Apps}/inventory/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var exported = await JsonOf(res);
        exported.GetProperty("formatVersion").GetInt32().Should().Be(7);
        exported.GetProperty("events").GetArrayLength().Should().Be(6);
        exported.GetProperty("checks").GetArrayLength().Should().Be(1);
        exported.GetProperty("ledgers").GetArrayLength().Should().Be(1);
        exported.GetProperty("conversions").GetArrayLength().Should().Be(1);
        exported.GetProperty("reports").GetArrayLength().Should().Be(5);
        exported.GetProperty("printTemplates").GetArrayLength().Should().Be(1);

        // 匯入到另一個空資料庫：內建套件的代號不能被匯入佔用，所以換一個應用程式代號；模組、事件、檢查的鍵照舊
        var node = JsonNode.Parse(exported.GetRawText())!;
        node["manifest"]!["id"] = "inventory-copy";
        using var fresh = new TestApp();
        var admin2 = fresh.AsUser();
        var imported = await admin2.PostAsync($"{Apps}/packages/import", new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"));
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        var rig = await OpenAsync(admin2, assignMembers: false, packageId: "inventory-copy", groupMap: new
        {
            groupMap = exported.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("code").GetString()!, _ => (object)new { mode = "create" })
        });

        var item = await rig.ItemAsync("軸承");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var customer = await rig.CustomerAsync("立新機械");
        await rig.OpeningAsync(wh, item, 5);
        await rig.TransitionOk(admin2, await rig.ShipmentDraftAsync(customer, wh, item, 3), "shipment", "已出貨");
        (await rig.TransitionAsync(admin2, await rig.ShipmentDraftAsync(customer, wh, item, 3), "shipment", "已出貨"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "檢查在匯入後仍有效");
        (await rig.BalanceAsync(item, wh)).Should().Be(2m);
        var move = (await rig.MovesAsync()).First();
        (await admin2.DeleteAsync($"{Base}/records/{Id(move)}?rowVersion={Uri.EscapeDataString(move.GetProperty("rowVersion").GetString()!)}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "分錄在匯入後仍不可刪");
        var printed = await admin2.GetAsync($"{Base}/records/{Id(await rig.ShipmentDraftAsync(customer, wh, item, 1))}/print/inv-ship-sheet?locale=zh-TW");
        printed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrintShipment_HasHeaderLinesAndTotal_AndValuesAreEscaped()
    {
        var rig = await OpenAsync(_admin);
        var item = await rig.ItemAsync("<img src=x onerror=alert(1)>感測器", price: 1200);
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var customer = await rig.CustomerAsync("<script>alert(1)</script>宏達");
        await rig.OpeningAsync(wh, item, 20);
        var shipment = await rig.ShipmentDraftAsync(customer, wh, item, 10);

        var templates = await JsonOf(await _admin.GetAsync($"{Base}/modules/{rig.Key("shipment")}/print-templates"));
        templates.EnumerateArray().Select(t => t.GetProperty("key").GetString()).Should().Contain("inv-ship-sheet");
        var res = await _admin.GetAsync($"{Base}/records/{Id(shipment)}/print/inv-ship-sheet?locale=zh-TW");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("SH-").And.Contain("成品倉").And.Contain("感測器");
        html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
        html.Should().NotContain("<img src=x").And.Contain("&lt;img");
        html.Should().Contain(">1,200<").And.Contain(">12,000<").And.Contain("合計（未稅）：12,000");
        html.Should().Contain("<td align=\"right\">10</td>");
    }
}
