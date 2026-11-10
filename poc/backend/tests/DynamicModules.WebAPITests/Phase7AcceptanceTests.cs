using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ImportService;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 7 階段驗收情境：選單分區、草稿預覽、記錄歷程、匯出 CSV、匯入預覽與確認、SetNull 清空的歷程。</summary>
public class Phase7AcceptanceTests : IClassFixture<TestApp>
{
    private const string Order = "order";
    private const string Line = "order-line";
    private const string Visit = "visit";

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public Phase7AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        app.Clock.Fixed = new DateTime(2026, 11, 5, 9, 0, 0);
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private async Task<(long Id, string Key)> AddField(string module, long blockId, string type, string label, object config)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/fields",
            new { blockId, type, label = Zh(label), config });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!);
    }

    private async Task<long> AddOption(long fieldId, string label)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label) });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    /// <summary>建模組（可帶選單分區），並把預設的標題欄位改名；回傳區塊 id 與標題欄位 id。</summary>
    private async Task<(long BlockId, long TitleId)> NewModule(string key, string name, string titleLabel, bool titleRequired, string? section = null)
    {
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key, name = Zh(name), section = section is null ? null : Zh(section) })).StatusCode
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

    private static object Priv(bool read = false, bool add = false, bool edit = false, bool delete = false,
        bool printExport = false, bool pricing = false) =>
        new { read, add, edit, delete, printExport, pricing, approve = false };

    private static object Row(object values, long? id = null, string? rowVersion = null, bool delete = false) =>
        new { id, values, rowVersion, _delete = delete };

    private static Dictionary<string, object[]> Details(params object[] rows) => new() { [Line] = rows };

    private static string Rv(JsonElement rec) => rec.GetProperty("rowVersion").GetString()!;
    private static JsonElement Rows(JsonElement rec) => rec.GetProperty("details").GetProperty(Line);
    private static long IdOf(JsonElement rec) => rec.GetProperty("id").GetInt64();
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private async Task<JsonElement> Put(HttpClient c, long id, string rowVersion, object? values, object? details)
    {
        var res = await c.PutAsJsonAsync($"{Base}/records/{id}", new { values = values ?? new { }, rowVersion, details });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await JsonOf(res);
    }

    private async Task<List<JsonElement>> History(HttpClient c, long id)
    {
        var res = await c.GetAsync($"{Base}/records/{id}/history?currentPage=1&pagesSize=50");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await JsonOf(res)).GetProperty("items").EnumerateArray().ToList();
    }

    private static MultipartFormDataContent Upload(byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", "import.csv" } };
    }

    /// <summary>每一步把假時鐘往前推一分鐘，讓歷程的時間先後分得出來。</summary>
    private void Tick() => _app.Clock.Fixed = _app.Clock.Fixed!.Value.AddMinutes(1);

    [Fact]
    public async Task Phase7_Scenario()
    {
        // 1. 訂單（分區「業務」）與明細，同第 6 階段
        var (orderBlock, _) = await NewModule(Order, "訂單", "備註", false, section: "業務");
        var no = await AddField(Order, orderBlock, "autonumber", "訂單編號", new { format = "SO-{yyyyMM}-{0000}" });
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Order}", new { name = Zh("訂單"), section = Zh("業務"), titleFieldId = no.Id }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var customer = await AddField(Order, orderBlock, "reference", "客戶",
            new { refTarget = new { kind = "entity", key = "sales.customer" } });
        var orderDate = await AddField(Order, orderBlock, "date", "訂單日期", new { });
        var status = await AddField(Order, orderBlock, "enum", "狀態", new { });
        var draft = $"opt_{await AddOption(status.Id, "草稿")}";
        var confirmed = $"opt_{await AddOption(status.Id, "已確認")}";

        var (lineBlock, lineTitleId) = await NewModule(Line, "訂單明細", "品名", true);
        var parentRef = await AddField(Line, lineBlock, "reference", "訂單",
            new { refTarget = new { kind = "module", key = Order }, relation = "detail" });
        var qty = await AddField(Line, lineBlock, "decimal", "數量", new { scale = 0 });
        var price = await AddField(Line, lineBlock, "currency", "單價", new { scale = 0 });
        var amount = await AddField(Line, lineBlock, "formula", "金額",
            new { formula = $"{{{qty.Id}}} * {{{price.Id}}}", resultType = "currency", scale = 0 });
        var lineTitle = $"f_{lineTitleId}";
        var sum = await AddField(Order, orderBlock, "rollup", "未稅合計",
            new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = amount.Id } });

        // 2. 草稿：設計器預覽 200、執行期 schema 404
        var preview = await _admin.GetAsync($"{Base}/modules/{Order}/schema/preview");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(preview)).GetProperty("blocks")[0].GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString()).Should().Contain([no.Key, status.Key, sum.Key]);
        (await _admin.GetAsync($"{Base}/modules/{Order}/schema")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await Activate(_admin, Order);
        await Activate(_admin, Line);
        var menu = await JsonOf(await _admin.GetAsync($"{Base}/menu"));
        var orderItem = menu.EnumerateArray().Single(m => m.GetProperty("key").GetString() == Order);
        orderItem.GetProperty("section").GetProperty("zh-TW").GetString().Should().Be("業務");

        // 權限：業務 Own、沒有 Pricing 與 PrintExport；業務主管 All、全部動作
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        long G(string key) => groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == key).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Order}/permissions", new
        {
            groups = new object[]
            {
                new { groupId = G("sales"), privileges = Priv(read: true, add: true, edit: true), scope = "own" },
                new { groupId = G("sales-manager"), privileges = Priv(true, true, true, true, true, true), scope = "all" }
            },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var sales = _app.AsUser("sales01");
        var manager = _app.AsUser("manager01");

        // 3. 業務建立含 2 列明細的訂單，再改狀態、改一列數量；主管補單價；業務刪一列
        var createdRes = await sales.PostAsJsonAsync($"{Base}/modules/{Order}/records", new
        {
            values = new Dictionary<string, object>
            {
                [customer.Key] = "C-001", [orderDate.Key] = "2026-11-05", [status.Key] = draft
            },
            details = Details(
                Row(new Dictionary<string, object> { [lineTitle] = "甲", [qty.Key] = 2 }),
                Row(new Dictionary<string, object> { [lineTitle] = "乙", [qty.Key] = 1 }))
        });
        createdRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await JsonOf(createdRes);
        var orderId = IdOf(order);
        var rowA = IdOf(Rows(order)[0]);
        var rowB = IdOf(Rows(order)[1]);

        Tick();
        var s1 = await Put(sales, orderId, Rv(order), new Dictionary<string, object> { [status.Key] = confirmed }, null);
        Tick();
        var s2 = await Put(sales, orderId, Rv(s1), null, Details(
            Row(new Dictionary<string, object> { [qty.Key] = 3 }, rowA, Rv(Rows(s1)[0]))));
        Tick();
        var m1 = await Put(manager, orderId, Rv(s2), null, Details(
            Row(new Dictionary<string, object> { [price.Key] = 100 }, rowA, Rv(Rows(s2)[0]))));
        Tick();
        await Put(sales, orderId, Rv(m1), null, Details(Row(new { }, rowB, Rv(Rows(m1)[1]), delete: true)));

        var hist = await History(manager, orderId);
        hist.Select(h => h.GetProperty("changedTime").GetDateTime()).Should().BeInDescendingOrder();
        hist.Should().HaveCount(7);
        string Sig(JsonElement h) =>
            $"{h.GetProperty("operation").GetString()}|{Str(h, "fieldKey")}|{(h.TryGetProperty("detail", out var d) ? d.GetProperty("recordId").GetInt64() : orderId)}";
        hist.Take(4).Select(Sig).Should().Equal(
            $"delete||{rowB}",
            $"update|{price.Key}|{rowA}",
            $"update|{qty.Key}|{rowA}",
            $"update|{status.Key}|{orderId}");
        hist.Skip(4).Select(Sig).Should().BeEquivalentTo([$"create||{orderId}", $"create||{rowA}", $"create||{rowB}"]);
        var statusEntry = hist[3];
        (Str(statusEntry, "oldText"), Str(statusEntry, "newText")).Should().Be(("草稿", "已確認"));
        Str(statusEntry, "changedUserId").Should().Be("sales01");
        (Str(hist[2], "oldText"), Str(hist[2], "newText")).Should().Be(("2", "3"));
        hist[2].GetProperty("detail").GetProperty("moduleKey").GetString().Should().Be(Line);
        hist.Should().NotContain(h => Str(h, "fieldKey") == sum.Key || Str(h, "fieldKey") == amount.Key,
            "彙總與公式是系統計算，不記歷程");

        // 4. 業務沒有 Pricing：看不到單價那一列
        var salesHist = await History(sales, orderId);
        salesHist.Should().HaveCount(6);
        salesHist.Should().NotContain(h => Str(h, "fieldKey") == price.Key || Str(h, "fieldKey") == amount.Key);

        // 5. 主管匯出 CSV：BOM、欄位標籤、狀態是標籤；業務沒有 PrintExport → 403
        var export = await manager.GetAsync($"{Base}/modules/{Order}/export?format=csv");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var csv = CsvReader.Read(bytes);
        csv.Error.Should().BeNull();
        csv.Header.Should().Contain(["訂單編號", "客戶", "訂單日期", "狀態", "未稅合計"]);
        csv.Rows.Should().HaveCount(1);
        var cells = csv.Rows[0].Cells;
        string Cell(IReadOnlyList<string> r, string label) => r[csv.Header.ToList().IndexOf(label)];
        Cell(cells, "狀態").Should().Be("已確認");
        Cell(cells, "訂單編號").Should().Be("SO-202611-0001");
        Cell(cells, "未稅合計").Should().Be("300");
        (await sales.GetAsync($"{Base}/modules/{Order}/export?format=csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 6. 匯出檔改兩列後匯入：第 3 行狀態打錯 → 錯誤在 rows[3]；修正後建立 2 筆
        List<string> Edited(string date, string statusText)
        {
            var r = cells.ToList();
            r[csv.Header.ToList().IndexOf("訂單日期")] = date;
            r[csv.Header.ToList().IndexOf("狀態")] = statusText;
            return r;
        }
        var bad = CsvWriter.Write(csv.Header, [Edited("2026-11-06", "草稿"), Edited("2026-11-07", "已確認X")]);
        var check = await manager.PostAsync($"{Base}/modules/{Order}/import?commit=false", Upload(bad));
        check.StatusCode.Should().Be(HttpStatusCode.OK);
        var p = await JsonOf(check);
        (p.GetProperty("total").GetInt32(), p.GetProperty("valid").GetInt32()).Should().Be((2, 1));
        var err = p.GetProperty("errors").EnumerateArray().Single();
        err.GetProperty("line").GetInt32().Should().Be(3);
        err.GetProperty("errors").GetProperty(status.Key)[0].GetString().Should().Be("找不到選項「已確認X」");
        p.GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("header").GetString() == "訂單編號")
            .GetProperty("ignoredReason").GetString().Should().Be("系統計算的欄位");

        Tick();
        var good = CsvWriter.Write(csv.Header, [Edited("2026-11-06", "草稿"), Edited("2026-11-07", "已確認")]);
        var commit = await manager.PostAsync($"{Base}/modules/{Order}/import?commit=true", Upload(good));
        commit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(commit)).GetProperty("created").GetInt32().Should().Be(2);
        var paged = await JsonOf(await manager.GetAsync($"{Base}/modules/{Order}/records/paged?currentPage=1&pagesSize=50"));
        var all = paged.GetProperty("items").EnumerateArray().ToList();
        all.Should().HaveCount(3);
        all.Select(r => r.GetProperty("values").GetProperty(no.Key).GetString()).Should()
            .BeEquivalentTo(["SO-202611-0001", "SO-202611-0002", "SO-202611-0003"]);
        var imported = all.Where(r => IdOf(r) != orderId).ToList();
        foreach (var r in imported)
        {
            var h = await History(manager, IdOf(r));
            h.Should().ContainSingle();
            Str(h[0], "operation").Should().Be("create");
            Str(h[0], "changedUserId").Should().Be("manager01");
        }

        // 7. 拜訪紀錄以 SetNull 參照訂單；刪掉訂單，拜訪紀錄多一列 Update
        var (visitBlock, visitTitleId) = await NewModule(Visit, "拜訪紀錄", "內容", true);
        var visitRef = await AddField(Visit, visitBlock, "reference", "訂單",
            new { refTarget = new { kind = "module", key = Order }, onDelete = "setNull" });
        await Activate(_admin, Visit);
        var target = imported[0];
        var visitRes = await _admin.PostAsJsonAsync($"{Base}/modules/{Visit}/records", new
        {
            values = new Dictionary<string, object> { [$"f_{visitTitleId}"] = "拜訪", [visitRef.Key] = IdOf(target) }
        });
        visitRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var visitId = IdOf(await JsonOf(visitRes));

        Tick();
        var targetNow = await JsonOf(await manager.GetAsync($"{Base}/records/{IdOf(target)}"));
        (await manager.DeleteAsync($"{Base}/records/{IdOf(target)}?rowVersion={Rv(targetNow)}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var visitHist = await History(_admin, visitId);
        visitHist.Should().HaveCount(2);
        var cleared = visitHist[0];
        (Str(cleared, "operation"), Str(cleared, "fieldKey")).Should().Be(("update", visitRef.Key));
        // 參照標題依目前資料解析（決策 4）：目標已刪，舊值顯示「（已刪除）」而不是當時的訂單編號
        Str(cleared, "oldText").Should().Be("（已刪除）");
        Str(cleared, "newText").Should().BeNullOrEmpty();
        Str(cleared, "changedUserId").Should().Be("manager01");
        Str(visitHist[1], "operation").Should().Be("create");
    }
}
