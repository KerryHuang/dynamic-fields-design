using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 6 階段驗收情境：訂單表頭與明細、彙總欄位、明細隱藏與權限沿用、一般參照的彙總。</summary>
public class Phase6AcceptanceTests : IClassFixture<TestApp>
{
    private const string Order = "order";
    private const string Line = "order-line";

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public Phase6AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        app.Clock.Fixed = new DateTime(2026, 11, 5, 10, 0, 0);
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private async Task<(long Id, string Key, JsonElement Body)> AddField(string module, long blockId, string type, string label, object config)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/fields",
            new { blockId, type, label = Zh(label), config });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!, f);
    }

    private async Task<long> AddOption(long fieldId, string label)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label) });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    /// <summary>建模組，並把預設的標題欄位改名成指定名稱；回傳區塊 id 與標題欄位 id。</summary>
    private async Task<(long BlockId, long TitleId)> NewModule(string key, string name, string titleLabel, bool titleRequired)
    {
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key, name = Zh(name) })).StatusCode
            .Should().Be(HttpStatusCode.Created, key);
        var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        var blockId = m.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var titleId = m.GetProperty("titleFieldId").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/fields/{titleId}", new
        {
            type = "text", label = Zh(titleLabel), config = new { required = titleRequired }, blockId
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        return (blockId, titleId);
    }

    private static object Rule(string kind, string[] targets, object condition) =>
        new { kind, condition, targets, optionIds = (long[]?)null, message = kind == "validation" ? Zh("交期不得早於訂單日期") : null, active = true };

    private static object Priv(bool read = false, bool add = false, bool edit = false, bool pricing = false) =>
        new { read, add, edit, delete = false, printExport = false, pricing, approve = false };

    private static object Row(object values, long? id = null, string? rowVersion = null, bool delete = false) =>
        new { id, values, rowVersion, _delete = delete };

    private static Dictionary<string, object[]> Details(params object[] rows) => new() { [Line] = rows };

    private Task<HttpResponseMessage> Post(HttpClient c, object body) =>
        c.PostAsJsonAsync($"{Base}/modules/{Order}/records", body);

    private Task<HttpResponseMessage> Put(HttpClient c, long id, string rowVersion, object? values, object? details) =>
        c.PutAsJsonAsync($"{Base}/records/{id}", new { values = values ?? new { }, rowVersion, details });

    private async Task<JsonElement> Get(HttpClient c, long id) => await JsonOf(await c.GetAsync($"{Base}/records/{id}"));

    private static string Rv(JsonElement rec) => rec.GetProperty("rowVersion").GetString()!;
    private static decimal Num(JsonElement rec, string key) => rec.GetProperty("values").GetProperty(key).GetDecimal();
    private static JsonElement Rows(JsonElement rec) => rec.GetProperty("details").GetProperty(Line);

    private static async Task<string> ErrorOf(HttpResponseMessage res, string errorKey)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.TryGetProperty(errorKey, out var e).Should().BeTrue(errors.ToString());
        return e[0].GetString()!;
    }

    [Fact]
    public async Task Phase6_Scenario()
    {
        // 1. order：訂單編號（自動編號，當標題）、客戶、訂單日期、交期、狀態、稅率
        var (orderBlock, orderTitleId) = await NewModule(Order, "訂單", "備註", false);
        var no = await AddField(Order, orderBlock, "autonumber", "訂單編號", new { format = "SO-{yyyyMM}-{0000}" });
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Order}", new { name = Zh("訂單"), titleFieldId = no.Id }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var customer = await AddField(Order, orderBlock, "reference", "客戶",
            new { refTarget = new { kind = "entity", key = "sales.customer" } });
        var orderDate = await AddField(Order, orderBlock, "date", "訂單日期", new { });
        var dueDate = await AddField(Order, orderBlock, "date", "交期", new { });
        var status = await AddField(Order, orderBlock, "enum", "狀態", new { });
        await AddOption(status.Id, "草稿");
        var closed = await AddOption(status.Id, "已結案");
        var rate = await AddField(Order, orderBlock, "percent", "稅率", new { defaultValue = 5 });
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Order}/rules", new
        {
            rules = new object[]
            {
                Rule("validation", [$"field:{dueDate.Id}"], new
                {
                    any = new object[]
                    {
                        new { field = dueDate.Id, op = "isEmpty" },
                        new { field = dueDate.Id, op = "gte", value = new { fieldRef = orderDate.Id } }
                    }
                }),
                Rule("readOnly", ["*"], new { field = status.Id, op = "eq", value = $"opt_{closed}" })
            }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. order-line：訂單（明細）、項次、品名、數量、單價、金額
        var (lineBlock, lineTitleId) = await NewModule(Line, "訂單明細", "品名", true);
        var parentRef = await AddField(Line, lineBlock, "reference", "訂單",
            new { refTarget = new { kind = "module", key = Order }, relation = "detail" });
        var cfg = parentRef.Body.GetProperty("config");
        cfg.GetProperty("required").GetBoolean().Should().BeTrue();
        cfg.GetProperty("onDelete").GetString().Should().Be("cascade");
        var seq = await AddField(Line, lineBlock, "autonumber", "項次", new { format = "{0}", scope = "parent" });
        var qty = await AddField(Line, lineBlock, "decimal", "數量", new { scale = 0 });
        var price = await AddField(Line, lineBlock, "currency", "單價", new { scale = 0 });
        var amount = await AddField(Line, lineBlock, "formula", "金額",
            new { formula = $"{{{qty.Id}}} * {{{price.Id}}}", resultType = "currency", scale = 0 });
        var lineTitle = $"f_{lineTitleId}";

        // 3. order 的彙總與公式；兩個模組都啟用
        var sum = await AddField(Order, orderBlock, "rollup", "未稅合計",
            new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = amount.Id } });
        sum.Body.GetProperty("config").GetProperty("resultType").GetString().Should().Be("currency");
        var count = await AddField(Order, orderBlock, "rollup", "明細筆數",
            new { rollup = new { sourceFieldId = parentRef.Id, function = "count" } });
        var tax = await AddField(Order, orderBlock, "formula", "稅額",
            new { formula = $"ROUND({{{sum.Id}}} * {{{rate.Id}}}, 0)", resultType = "currency", scale = 0 });
        var total = await AddField(Order, orderBlock, "formula", "含稅總額",
            new { formula = $"{{{sum.Id}}} + {{{tax.Id}}}", resultType = "currency", scale = 0 });
        await Activate(_admin, Order);
        await Activate(_admin, Line);
        var menu = (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).GetRawText();
        menu.Should().Contain($"\"{Order}\"").And.NotContain($"\"{Line}\"");

        // 4. 建立含 3 列明細的訂單
        var created = await Post(_admin, new
        {
            values = new Dictionary<string, object>
            {
                [customer.Key] = "C-001", [orderDate.Key] = "2026-11-05", [dueDate.Key] = "2026-11-20"
            },
            details = Details(
                Row(new Dictionary<string, object> { [lineTitle] = "甲", [qty.Key] = 2, [price.Key] = 100 }),
                Row(new Dictionary<string, object> { [lineTitle] = "乙", [qty.Key] = 1, [price.Key] = 50 }),
                Row(new Dictionary<string, object> { [lineTitle] = "丙", [qty.Key] = 4, [price.Key] = 25 }))
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await JsonOf(created);
        var orderId = order.GetProperty("id").GetInt64();
        order.GetProperty("values").GetProperty(no.Key).GetString().Should().Be("SO-202611-0001");
        Rows(order).EnumerateArray().Select(r => r.GetProperty("values").GetProperty(seq.Key).GetString())
            .Should().Equal("1", "2", "3");
        Rows(order).EnumerateArray().Select(r => r.GetProperty("values").GetProperty(amount.Key).GetDecimal())
            .Should().Equal(200m, 50m, 100m);
        (Num(order, sum.Key), Num(order, count.Key), Num(order, tax.Key), Num(order, total.Key))
            .Should().Be((350m, 3m, 18m, 368m));

        // 5. 改第 2 列數量為 3：450 / 23 / 473，表頭 RowVersion 換掉
        var row2 = Rows(order)[1];
        var edit = await Put(_admin, orderId, Rv(order), null, Details(
            Row(new Dictionary<string, object> { [qty.Key] = 3 }, row2.GetProperty("id").GetInt64(), Rv(row2))));
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = await JsonOf(edit);
        (Num(edited, sum.Key), Num(edited, tax.Key), Num(edited, total.Key)).Should().Be((450m, 23m, 473m));
        Rv(edited).Should().NotBe(Rv(order));

        // 6. 刪除第 3 列：350 / 2 筆；再新增一列，項次 4
        var row3 = Rows(edited)[2];
        var del = await Put(_admin, orderId, Rv(edited), null, Details(
            Row(new { }, row3.GetProperty("id").GetInt64(), Rv(row3), delete: true)));
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterDel = await JsonOf(del);
        (Num(afterDel, sum.Key), Num(afterDel, count.Key)).Should().Be((350m, 2m));
        var add = await Put(_admin, orderId, Rv(afterDel), null, Details(
            Row(new Dictionary<string, object> { [lineTitle] = "丁", [qty.Key] = 1, [price.Key] = 10 })));
        add.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterAdd = await JsonOf(add);
        Rows(afterAdd).GetArrayLength().Should().Be(3);
        Rows(afterAdd)[2].GetProperty("values").GetProperty(seq.Key).GetString().Should().Be("4");
        (Num(afterAdd, sum.Key), Num(afterAdd, count.Key)).Should().Be((360m, 3m));

        // 7. 交期早於訂單日期：400，表頭與明細都沒變
        var rowA = Rows(afterAdd)[0];
        var early = await Put(_admin, orderId, Rv(afterAdd), new Dictionary<string, object> { [dueDate.Key] = "2026-11-01" },
            Details(Row(new Dictionary<string, object> { [qty.Key] = 9 }, rowA.GetProperty("id").GetInt64(), Rv(rowA))));
        (await ErrorOf(early, dueDate.Key)).Should().Be("交期不得早於訂單日期");
        var unchanged = await Get(_admin, orderId);
        Rv(unchanged).Should().Be(Rv(afterAdd));
        Num(unchanged, sum.Key).Should().Be(360m);
        Num(Rows(unchanged)[0], qty.Key).Should().Be(2m);

        // 8. 明細數量不是數字：400，鍵 details.order-line[0].{數量鍵}
        var nan = await Put(_admin, orderId, Rv(unchanged), null, Details(
            Row(new Dictionary<string, object> { [qty.Key] = "abc" }, rowA.GetProperty("id").GetInt64(), Rv(rowA))));
        await ErrorOf(nan, $"details.{Line}[0].{qty.Key}");

        // 9. 狀態改已結案後，只改明細：400 _record
        var close = await Put(_admin, orderId, Rv(unchanged), new Dictionary<string, object> { [status.Key] = $"opt_{closed}" }, null);
        close.StatusCode.Should().Be(HttpStatusCode.OK);
        var closedOrder = await JsonOf(close);
        var rowB = Rows(closedOrder)[0];
        var locked = await Put(_admin, orderId, Rv(closedOrder), null, Details(
            Row(new Dictionary<string, object> { [qty.Key] = 5 }, rowB.GetProperty("id").GetInt64(), Rv(rowB))));
        (await ErrorOf(locked, "_record")).Should().Be("此記錄目前不可修改");

        // 10. 權限：sales 範圍 Own、沒有 Pricing；sales-manager 範圍 All、有 Pricing
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        long G(string key) => groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == key).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Order}/permissions", new
        {
            groups = new object[]
            {
                new { groupId = G("sales"), privileges = Priv(read: true, add: true, edit: true), scope = "own" },
                new { groupId = G("sales-manager"), privileges = Priv(read: true, add: true, edit: true, pricing: true), scope = "all" }
            },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var sales01 = _app.AsUser("sales01");
        var sales02 = _app.AsUser("sales02");
        var manager = _app.AsUser("manager01");
        var own = await Post(sales01, new
        {
            values = new Dictionary<string, object> { [customer.Key] = "C-002", [orderDate.Key] = "2026-11-05" },
            details = Details(Row(new Dictionary<string, object> { [lineTitle] = "戊", [qty.Key] = 2 }))
        });
        own.StatusCode.Should().Be(HttpStatusCode.Created);
        var ownRec = await JsonOf(own);
        var ownId = ownRec.GetProperty("id").GetInt64();
        var ownRowId = Rows(ownRec)[0].GetProperty("id").GetInt64();
        (await sales02.GetAsync($"{Base}/records/{ownId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales02.GetAsync($"{Base}/records/{ownRowId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        async Task<(HashSet<long> Header, HashSet<long> Lines)> SchemaIds(HttpClient c)
        {
            var s = await JsonOf(await c.GetAsync($"{Base}/modules/{Order}/schema"));
            var head = s.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
                .Select(f => f.GetProperty("id").GetInt64()).ToHashSet();
            var lines = s.GetProperty("details").EnumerateArray().SelectMany(d => d.GetProperty("fields").EnumerateArray())
                .Select(f => f.GetProperty("id").GetInt64()).ToHashSet();
            return (head, lines);
        }
        var (salesHead, salesLines) = await SchemaIds(sales01);
        salesHead.Should().NotContain([sum.Id, tax.Id, total.Id]).And.Contain(count.Id);
        salesLines.Should().NotContain([price.Id, amount.Id]).And.Contain(qty.Id);
        var (mgrHead, mgrLines) = await SchemaIds(manager);
        mgrHead.Should().Contain([sum.Id, tax.Id, total.Id]);
        mgrLines.Should().Contain([price.Id, amount.Id]);
        var mgrPaged = await JsonOf(await manager.GetAsync($"{Base}/modules/{Order}/records/paged?currentPage=1&pagesSize=50"));
        mgrPaged.GetProperty("totalCount").GetInt32().Should().Be(2);
        (await manager.GetAsync($"{Base}/records/{ownId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 11. 一般參照的彙總：維修紀錄 → 維修申請
        var (reqBlock, _) = await NewModule("repair-req", "維修申請", "申請人", true);
        var (logBlock, logTitleId) = await NewModule("repair-log", "維修紀錄", "處理內容", true);
        var reqRef = await AddField("repair-log", logBlock, "reference", "申請單",
            new { refTarget = new { kind = "module", key = "repair-req" } });
        var hours = await AddField("repair-log", logBlock, "decimal", "工時", new { scale = 1 });
        await Activate(_admin, "repair-log");
        var totalHours = await AddField("repair-req", reqBlock, "rollup", "總工時",
            new { rollup = new { sourceFieldId = reqRef.Id, function = "sum", aggregateFieldId = hours.Id } });
        var logCount = await AddField("repair-req", reqBlock, "rollup", "紀錄筆數",
            new { rollup = new { sourceFieldId = reqRef.Id, function = "count" } });
        await Activate(_admin, "repair-req");
        var reqTitle = await TitleKey(_admin, "repair-req");
        var logTitle = $"f_{logTitleId}";

        async Task<long> NewReq(string name)
        {
            var r = await _admin.PostAsJsonAsync($"{Base}/modules/repair-req/records", new { values = new Dictionary<string, object> { [reqTitle] = name } });
            r.StatusCode.Should().Be(HttpStatusCode.Created);
            var j = await JsonOf(r);
            (Num(j, totalHours.Key), Num(j, logCount.Key)).Should().Be((0m, 0m), "R13：新記錄的 count 與 sum 為 0");
            return j.GetProperty("id").GetInt64();
        }
        async Task<JsonElement> NewLog(string text, decimal h, long req)
        {
            var r = await _admin.PostAsJsonAsync($"{Base}/modules/repair-log/records",
                new { values = new Dictionary<string, object> { [logTitle] = text, [hours.Key] = h, [reqRef.Key] = req } });
            r.StatusCode.Should().Be(HttpStatusCode.Created);
            return await JsonOf(r);
        }
        async Task AssertReq(long id, decimal h, decimal n)
        {
            var j = await Get(_admin, id);
            (Num(j, totalHours.Key), Num(j, logCount.Key)).Should().Be((h, n));
        }
        var reqA = await NewReq("申請 A");
        var reqB = await NewReq("申請 B");
        await NewLog("甲", 1.5m, reqA);
        var log2 = await NewLog("乙", 2m, reqA);
        await AssertReq(reqA, 3.5m, 2);

        var move = await _admin.PutAsJsonAsync($"{Base}/records/{log2.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [logTitle] = "乙", [hours.Key] = 2m, [reqRef.Key] = reqB },
            rowVersion = Rv(log2)
        });
        move.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertReq(reqA, 1.5m, 1);
        await AssertReq(reqB, 2m, 1);

        var log2Now = await Get(_admin, log2.GetProperty("id").GetInt64());
        (await _admin.DeleteAsync($"{Base}/records/{log2Now.GetProperty("id").GetInt64()}?rowVersion={Rv(log2Now)}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertReq(reqB, 0m, 0);

        // 12. 刪除訂單：200，明細全部軟刪除
        var toDelete = await JsonOf(await Post(_admin, new
        {
            values = new Dictionary<string, object> { [orderDate.Key] = "2026-11-05" },
            details = Details(
                Row(new Dictionary<string, object> { [lineTitle] = "己", [qty.Key] = 1, [price.Key] = 1 }),
                Row(new Dictionary<string, object> { [lineTitle] = "庚", [qty.Key] = 1, [price.Key] = 1 }))
        }));
        var delId = toDelete.GetProperty("id").GetInt64();
        var lineIds = Rows(toDelete).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        lineIds.Should().HaveCount(2);
        (await _admin.DeleteAsync($"{Base}/records/{delId}?rowVersion={Rv(toDelete)}")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(r => lineIds.Contains(r.Id)).Select(r => r.DeleteFlag).ToList()
            .Should().Equal(true, true);
    }
}
