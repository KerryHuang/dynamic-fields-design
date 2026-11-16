using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 8 階段驗收情境：從內建範本建訂單（spec §13.2 Demo 情境一）、匯出範本後改代碼匯入再套用。</summary>
/// <remarks>每個測試一個新的資料庫：內建範本的模組代碼固定（order、order-line），共用會互相撞。</remarks>
public sealed class Phase8AcceptanceTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public Phase8AcceptanceTests()
    {
        _admin = _app.AsUser("admin");
        _app.Clock.Fixed = new DateTime(2026, 11, 5, 9, 0, 0);
    }

    public void Dispose() => _app.Dispose();

    /// <summary>一個模組的欄位鍵（依繁中標籤）與下拉選項值（依「欄位標籤/選項標籤」）。</summary>
    private sealed record Layout(Dictionary<string, string> Keys, Dictionary<string, string> Options)
    {
        public string this[string label] => Keys[label];
    }

    private async Task<Layout> LayoutOf(string module)
    {
        var res = await _admin.GetAsync($"{Base}/modules/{module}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, module);
        var fields = (await JsonOf(res)).GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        string Label(JsonElement e) => e.GetProperty("label").GetProperty("zh-TW").GetString()!;
        var keys = fields.ToDictionary(Label, f => f.GetProperty("key").GetString()!);
        var options = fields
            .SelectMany(f => f.GetProperty("options").EnumerateArray().Select(o => (Field: Label(f), Option: o)))
            .ToDictionary(x => $"{x.Field}/{Label(x.Option)}", x => x.Option.GetProperty("value").GetString()!);
        return new Layout(keys, options);
    }

    private static object Row(object values, long? id = null, string? rowVersion = null, bool delete = false) =>
        new { id, values, rowVersion, _delete = delete };

    private static Dictionary<string, object[]> Details(string line, params object[] rows) => new() { [line] = rows };

    private static string Rv(JsonElement rec) => rec.GetProperty("rowVersion").GetString()!;
    private static long IdOf(JsonElement rec) => rec.GetProperty("id").GetInt64();
    private static decimal Num(JsonElement rec, string key) => rec.GetProperty("values").GetProperty(key).GetDecimal();
    private static string? Str(JsonElement rec, string key) => rec.GetProperty("values").GetProperty(key).GetString();
    private static JsonElement Rows(JsonElement rec, string line) => rec.GetProperty("details").GetProperty(line);

    private static Task<HttpResponseMessage> Put(HttpClient c, long id, string rowVersion, object? values, object? details) =>
        c.PutAsJsonAsync($"{Base}/records/{id}", new { values = values ?? new { }, rowVersion, details });

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await call;
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res, string errorKey)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.TryGetProperty(errorKey, out var e).Should().BeTrue(errors.ToString());
        return e[0].GetString()!;
    }

    [Fact]
    public async Task Phase8_DemoScenario1()
    {
        const string order = "order";
        const string line = "order-line";

        // 1. admin 從內建範本建立訂單（預設代碼），兩個模組都啟用
        await Ok(_admin.PostAsJsonAsync($"{Base}/templates/order/instantiate", new { }), HttpStatusCode.Created);
        await Activate(_admin, order);
        await Activate(_admin, line);
        var h = await LayoutOf(order);
        var l = await LayoutOf(line);

        // 2. sales01 建訂單：不給訂單日期、交期 = 今天 + 7 天、3 列明細不給交期
        var sales01 = _app.AsUser("sales01");
        var manager = _app.AsUser("manager01");
        object Line(string name, int qty, int price) => Row(new Dictionary<string, object>
        {
            [l["品名"]] = name, [l["數量"]] = qty, [l["單價"]] = price
        });
        var created = await Ok(sales01.PostAsJsonAsync($"{Base}/modules/{order}/records", new
        {
            values = new Dictionary<string, object> { [h["客戶"]] = "E-0001", [h["交期"]] = "2026-11-12" },
            details = Details(line, Line("甲", 2, 100), Line("乙", 3, 300), Line("丙", 1, 1000))
        }), HttpStatusCode.Created);
        var orderId = IdOf(created);
        Str(created, h["訂單編號"]).Should().Be("SO-202611-0001");
        Str(created, h["訂單日期"]).Should().Be("2026-11-05", "訂單日期預設營運今天");
        Str(created, h["狀態"]).Should().Be(h.Options["狀態/草稿"]);
        Rows(created, line).EnumerateArray().Select(r => Str(r, l["項次"])).Should().Equal("1", "2", "3");
        Rows(created, line).EnumerateArray().Select(r => Str(r, l["交期"])).Should().Equal("2026-11-12", "2026-11-12", "2026-11-12");

        // 3. 業務有金額軸（R10）：sales01 建立時的單價直接生效；sales01、manager01 讀都是 2100 / 105 / 2205，明細 3 筆
        (Num(created, h["未稅合計"]), Num(created, h["稅額"]), Num(created, h["含稅總額"])).Should().Be((2100m, 105m, 2205m));
        var read = await JsonOf(await manager.GetAsync($"{Base}/records/{orderId}"));
        (Num(read, h["未稅合計"]), Num(read, h["稅額"]), Num(read, h["含稅總額"]), Num(read, h["明細筆數"]))
            .Should().Be((2100m, 105m, 2205m, 3m));
        Rows(read, line).EnumerateArray().Select(r => Num(r, l["金額"])).Should().Equal(200m, 900m, 1000m);

        // 4. 第一列數量改 5：2400 / 120 / 2520
        var row1 = Rows(read, line)[0];
        var edited = await Ok(Put(manager, orderId, Rv(read), null, Details(line,
            Row(new Dictionary<string, object> { [l["數量"]] = 5 }, IdOf(row1), Rv(row1)))));
        (Num(edited, h["未稅合計"]), Num(edited, h["稅額"]), Num(edited, h["含稅總額"])).Should().Be((2400m, 120m, 2520m));

        // 5. 刪第三列：1400 / 70，明細 2 筆
        var row3 = Rows(edited, line)[2];
        var afterDel = await Ok(Put(manager, orderId, Rv(edited), null, Details(line,
            Row(new { }, IdOf(row3), Rv(row3), delete: true))));
        (Num(afterDel, h["未稅合計"]), Num(afterDel, h["稅額"]), Num(afterDel, h["明細筆數"])).Should().Be((1400m, 70m, 2m));

        // 6. 交期改成訂單日期前一天 → 400
        var early = await Put(manager, orderId, Rv(afterDel), new Dictionary<string, object> { [h["交期"]] = "2026-11-04" }, null);
        (await ErrorOf(early, h["交期"])).Should().Be("交期不得早於訂單日期");

        // 7. 改成「取消」不給原因 → 400；給原因 → 200；再改任何欄位 → 400 _record（同第 4、6 階段的斷言）
        var cancel = h.Options["狀態/取消"];
        var noReason = await Put(manager, orderId, Rv(afterDel), new Dictionary<string, object> { [h["狀態"]] = cancel }, null);
        await ErrorOf(noReason, h["取消原因"]);
        var cancelled = await Ok(Put(manager, orderId, Rv(afterDel),
            new Dictionary<string, object> { [h["狀態"]] = cancel, [h["取消原因"]] = "客戶撤單" }, null));
        var locked = await Put(manager, orderId, Rv(cancelled), new Dictionary<string, object> { [h["備註"]] = "再改" }, null);
        (await ErrorOf(locked, "_record")).Should().Be("此記錄目前不可修改");

        // 8. 另一張訂單結案後同樣全單唯讀（只改明細也被拒）
        var second = await Ok(manager.PostAsJsonAsync($"{Base}/modules/{order}/records", new
        {
            values = new Dictionary<string, object> { [h["客戶"]] = "E-0002" },
            details = Details(line, Line("丁", 1, 10))
        }), HttpStatusCode.Created);
        var closed = await Ok(Put(manager, IdOf(second), Rv(second),
            new Dictionary<string, object> { [h["狀態"]] = h.Options["狀態/已結案"] }, null));
        var closedRow = Rows(closed, line)[0];
        var lockedLine = await Put(manager, IdOf(second), Rv(closed), null, Details(line,
            Row(new Dictionary<string, object> { [l["數量"]] = 9 }, IdOf(closedRow), Rv(closedRow))));
        (await ErrorOf(lockedLine, "_record")).Should().Be("此記錄目前不可修改");

        // 9. sales02 自己建的訂單看得到，看不到 sales01 的
        var sales02 = _app.AsUser("sales02");
        var mine = await Ok(sales02.PostAsJsonAsync($"{Base}/modules/{order}/records", new
        {
            values = new Dictionary<string, object> { [h["客戶"]] = "E-0003" },
            details = Details(line, Line("戊", 1, 50))
        }), HttpStatusCode.Created);
        var paged = await JsonOf(await sales02.GetAsync($"{Base}/modules/{order}/records/paged?currentPage=1&pagesSize=50"));
        paged.GetProperty("items").EnumerateArray().Select(IdOf).Should().Equal(IdOf(mine));
        (await sales02.GetAsync($"{Base}/records/{orderId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 10. 現場人員 floor01 沒有金額軸：schema 與記錄沒有金額類欄位，但讀得到 sales01 的訂單（資料範圍全部）
        var floor01 = _app.AsUser("floor01");
        var schema = await JsonOf(await floor01.GetAsync($"{Base}/modules/{order}/schema"));
        var headKeys = schema.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("key").GetString()).ToList();
        var lineKeys = schema.GetProperty("details").EnumerateArray().SelectMany(d => d.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("key").GetString()).ToList();
        string[] money = [h["未稅合計"], h["稅額"], h["含稅總額"]];
        string[] lineMoney = [l["單價"], l["金額"]];
        headKeys.Should().NotContain(money).And.Contain([h["明細筆數"], h["稅率"]]);
        lineKeys.Should().NotContain(lineMoney).And.Contain(l["數量"]);
        var seen = await Ok(floor01.GetAsync($"{Base}/records/{orderId}"));
        seen.GetProperty("values").EnumerateObject().Select(p => p.Name).Should().NotContain(money).And.Contain(h["明細筆數"]);
        Rows(seen, line).EnumerateArray().SelectMany(r => r.GetProperty("values").EnumerateObject().Select(p => p.Name))
            .Should().NotContain(lineMoney).And.Contain(l["數量"]);
    }

    [Fact]
    public async Task Phase8_ExportImportScenario()
    {
        // 匯出 order → 改範本代碼匯入 → 以 order-b／order-b-line 套用並啟用
        await Ok(_admin.PostAsJsonAsync($"{Base}/templates/order/instantiate", new { }), HttpStatusCode.Created);
        var export = await _admin.GetAsync($"{Base}/modules/order/export-template");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var pkg = JsonNode.Parse(await export.Content.ReadAsStringAsync())!.AsObject();
        pkg["key"] = "order-copy";
        await Ok(_admin.PostAsync($"{Base}/templates/import",
            new StringContent(pkg.ToJsonString(), Encoding.UTF8, "application/json")), HttpStatusCode.Created);

        var inst = await Ok(_admin.PostAsJsonAsync($"{Base}/templates/order-copy/instantiate", new
        {
            modules = new[] { new { code = "order", key = "order-b" }, new { code = "order-line", key = "order-b-line" } }
        }), HttpStatusCode.Created);
        inst.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString())
            .Should().Equal("order-b", "order-b-line");
        await Activate(_admin, "order-b");
        await Activate(_admin, "order-b-line");

        // 新模組建一張含 1 列明細的訂單：2 × 150 = 300，稅額 15，含稅 315
        var h = await LayoutOf("order-b");
        var l = await LayoutOf("order-b-line");
        var rec = await Ok(_admin.PostAsJsonAsync($"{Base}/modules/order-b/records", new
        {
            values = new Dictionary<string, object> { [h["客戶"]] = "E-0001", [h["交期"]] = "2026-11-20" },
            details = Details("order-b-line", Row(new Dictionary<string, object>
            {
                [l["品名"]] = "甲", [l["數量"]] = 2, [l["單價"]] = 150
            }))
        }), HttpStatusCode.Created);
        (Num(rec, h["未稅合計"]), Num(rec, h["稅額"]), Num(rec, h["含稅總額"]), Num(rec, h["明細筆數"]))
            .Should().Be((300m, 15m, 315m, 1m));
        Str(rec, h["訂單編號"]).Should().Be("SO-202611-0001");
        Str(Rows(rec, "order-b-line")[0], l["交期"]).Should().Be("2026-11-20");
    }
}
