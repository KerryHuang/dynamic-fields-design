using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>Demo 資料用的三個內建範本：故障報修、模具保養＋客戶擴充、品質異常。</summary>
/// <remarks>每個測試一個新的資料庫：內建範本的模組代碼固定，共用會互相撞。</remarks>
public sealed class DemoTemplatesTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public DemoTemplatesTests()
    {
        _admin = _app.AsUser("admin");
        _app.Clock.Fixed = new DateTime(2026, 11, 5, 9, 0, 0);
    }

    public void Dispose() => _app.Dispose();

    /// <summary>一個模組的欄位鍵（依繁中標籤）與下拉選項值（依「欄位標籤/選項標籤」）。</summary>
    private sealed record Layout(Dictionary<string, string> Keys, Dictionary<string, string> Options, List<JsonElement> Fields)
    {
        public string this[string label] => Keys[label];
        public JsonElement Field(string label) => Fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == label);
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
        return new Layout(keys, options, fields);
    }

    private Task<HttpResponseMessage> Instantiate(string key) =>
        _admin.PostAsJsonAsync($"{Base}/templates/{key}/instantiate", new { });

    private async Task InstantiateAndActivate(string template, params string[] modules)
    {
        var res = await Instantiate(template);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        foreach (var m in modules)
        {
            var a = await _admin.PostAsync($"{Base}/modules/{m}/activate", null);
            a.StatusCode.Should().Be(HttpStatusCode.OK, $"{m}: {await a.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await call;
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static Task<JsonElement> Create(HttpClient c, string module, Dictionary<string, object?> values) =>
        Ok(c.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values }), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> Put(HttpClient c, JsonElement rec, Dictionary<string, object?> values) =>
        c.PutAsJsonAsync($"{Base}/records/{IdOf(rec)}", new { values, rowVersion = rec.GetProperty("rowVersion").GetString() });

    private async Task<HttpResponseMessage> Delete(long id)
    {
        var rec = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        return await _admin.DeleteAsync($"{Base}/records/{id}?rowVersion={rec.GetProperty("rowVersion").GetString()}");
    }

    private static long IdOf(JsonElement rec) => rec.GetProperty("id").GetInt64();

    private static async Task<string> ErrorOf(HttpResponseMessage res, string errorKey)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.TryGetProperty(errorKey, out var e).Should().BeTrue(errors.ToString());
        return e[0].GetString()!;
    }

    private async Task<List<long>> ListIds(HttpClient c, string module) =>
        (await Ok(c.GetAsync($"{Base}/modules/{module}/records/paged?currentPage=1&pagesSize=50")))
            .GetProperty("items").EnumerateArray().Select(IdOf).ToList();

    /// <summary>報修的必填欄位：模具、申請人、故障說明（報修日期預設今天、狀態預設待處理）。</summary>
    private static Dictionary<string, object?> RepairValues(Layout r, string requester, Dictionary<string, object?>? extra = null)
    {
        var v = new Dictionary<string, object?> { [r["模具"]] = "E-0001", [r["申請人"]] = requester, [r["故障說明"]] = "冷卻水路漏水" };
        foreach (var (k, x) in extra ?? []) v[k] = x;
        return v;
    }

    [Fact]
    public async Task Instantiate_Repair_Standalone_ThenActivate()
    {
        var res = await Instantiate("repair");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("warnings").GetArrayLength().Should().Be(0);
        (await _admin.PostAsync($"{Base}/modules/repair/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Instantiate_MoldCare_Standalone_ThenActivateAll()
    {
        await InstantiateAndActivate("mold-care", "mold-info", "customer-info", "mold-care", "mold-care-part");

        var part = await LayoutOf("mold-care-part");
        part.Field("保養紀錄").GetProperty("config").GetProperty("onDelete").GetString().Should().Be("cascade");
        var care = await LayoutOf("mold-care");
        long Id(string label) => care.Field(label).GetProperty("id").GetInt64();
        care.Field("下次保養日").GetProperty("config").GetProperty("formula").GetString()
            .Should().Be($"DATEADD({{{Id("保養日期")}}}, {{{Id("保養週期（天）")}}})");
    }

    [Theory]
    [InlineData("mold-info", "上次保養日", "累計射出數")]
    [InlineData("customer-info", "信用等級", "業務窗口")]
    [InlineData("mold-care-part", "保養紀錄", "零件名稱")]
    public async Task Instantiate_MoldCare_MenuModulesHaveFilterableFields(string module, params string[] labels)
    {
        await InstantiateAndActivate("mold-care", "mold-info", "customer-info", "mold-care", "mold-care-part");

        var layout = await LayoutOf(module);
        foreach (var label in labels)
            layout.Field(label).GetProperty("config").GetProperty("isFilterable").GetBoolean().Should().BeTrue(label);
    }

    [Fact]
    public async Task Instantiate_Quality_WithoutOrderModule_400()
    {
        var res = await Instantiate("quality");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().NotBeEmpty().And.OnlyContain(k => k.StartsWith("quality.order."));
        (await _admin.GetAsync($"{Base}/modules/quality")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Instantiate_Quality_AfterOrder_ThenActivate()
    {
        await InstantiateAndActivate("order", "order", "order-line");
        await InstantiateAndActivate("quality", "quality");
    }

    [Fact]
    public async Task Repair_Rules_Enforced()
    {
        await InstantiateAndActivate("repair", "repair");
        var r = await LayoutOf("repair");
        var floor01 = _app.AsUser("floor01");
        var closed = r.Options["狀態/結案"];

        // 完成日期早於報修日期（預設今天 2026-11-05）
        var early = await floor01.PostAsJsonAsync($"{Base}/modules/repair/records",
            new { values = RepairValues(r, "floor01", new() { [r["完成日期"]] = "2026-11-01" }) });
        (await ErrorOf(early, r["完成日期"])).Should().Be("完成日期不得早於報修日期");

        // 結案但沒給處理說明、完成日期 → 必填
        var missing = await floor01.PostAsJsonAsync($"{Base}/modules/repair/records",
            new { values = RepairValues(r, "floor01", new() { [r["狀態"]] = closed }) });
        await ErrorOf(missing, r["處理說明"]);

        // 給齊 → 201；之後改任何欄位都被全單唯讀擋下
        var done = await Create(floor01, "repair", RepairValues(r, "floor01", new()
        {
            [r["狀態"]] = closed, [r["處理說明"]] = "更換 O 型環", [r["完成日期"]] = "2026-11-05"
        }));
        var locked = await Put(floor01, done, new() { [r["停機時數"]] = 2.5 });
        (await ErrorOf(locked, "_record")).Should().Be("此記錄目前不可修改");
    }

    [Fact]
    public async Task Repair_GroupScope()
    {
        await InstantiateAndActivate("repair", "repair");
        var r = await LayoutOf("repair");
        var floor01 = _app.AsUser("floor01");
        var maint01 = _app.AsUser("maint01");

        var byFloor = IdOf(await Create(floor01, "repair", RepairValues(r, "floor01")));
        var byMaint = IdOf(await Create(maint01, "repair", RepairValues(r, "maint01")));

        (await ListIds(_app.AsUser("floor02"), "repair")).Should().Equal(byFloor);
        (await ListIds(maint01, "repair")).Should().BeEquivalentTo([byFloor, byMaint]);
    }

    [Fact]
    public async Task Quality_OptionFilter()
    {
        await InstantiateAndActivate("order", "order", "order-line");
        await InstantiateAndActivate("quality", "quality");
        var q = await LayoutOf("quality");
        var qa01 = _app.AsUser("qa01");
        Dictionary<string, object?> Values(string cause) => new()
        {
            [q["異常類型"]] = q.Options["異常類型/外觀"], [q["不良原因"]] = q.Options[$"不良原因/{cause}"],
            [q["嚴重度"]] = q.Options["嚴重度/一般"]
        };

        var bad = await qa01.PostAsJsonAsync($"{Base}/modules/quality/records", new { values = Values("尺寸過大") });
        await ErrorOf(bad, q["不良原因"]);
        await Create(qa01, "quality", Values("刮傷"));
    }

    [Fact]
    public async Task DeleteBehaviors()
    {
        await InstantiateAndActivate("mold-care", "mold-info", "customer-info", "mold-care", "mold-care-part");
        await InstantiateAndActivate("repair", "repair");
        await InstantiateAndActivate("order", "order", "order-line");
        await InstantiateAndActivate("quality", "quality");

        // Cascade：刪保養紀錄，它的更換零件跟著刪
        var care = await LayoutOf("mold-care");
        var part = await LayoutOf("mold-care-part");
        var careRec = await Create(_admin, "mold-care", new() { [care["模具"]] = "E-0001", [care["保養週期（天）"]] = 90 });
        var partRec = await Create(_admin, "mold-care-part", new() { [part["保養紀錄"]] = IdOf(careRec), [part["零件名稱"]] = "頂針" });
        (await Delete(IdOf(careRec))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Base}/records/{IdOf(partRec)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // SetNull：刪被關聯的報修單，另一筆的關聯報修單變空
        var r = await LayoutOf("repair");
        var first = await Create(_admin, "repair", RepairValues(r, "floor01"));
        var second = await Create(_admin, "repair", RepairValues(r, "floor01", new() { [r["關聯報修單"]] = IdOf(first) }));
        (await Delete(IdOf(first))).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await Ok(_admin.GetAsync($"{Base}/records/{IdOf(second)}"));
        (after.GetProperty("values").TryGetProperty(r["關聯報修單"], out var related) && related.ValueKind != JsonValueKind.Null)
            .Should().BeFalse(after.ToString());

        // Restrict：被品質異常參照的訂單不能刪（回應同第 5 階段）
        var o = await LayoutOf("order");
        var l = await LayoutOf("order-line");
        var order = await Ok(_admin.PostAsJsonAsync($"{Base}/modules/order/records", new
        {
            values = new Dictionary<string, object> { [o["客戶"]] = "E-0001" },
            details = new Dictionary<string, object[]>
            {
                ["order-line"] = [new { values = new Dictionary<string, object> { [l["品名"]] = "外殼", [l["數量"]] = 1 } }]
            }
        }), HttpStatusCode.Created);
        var q = await LayoutOf("quality");
        await Create(_admin, "quality", new()
        {
            [q["訂單"]] = IdOf(order), [q["異常類型"]] = q.Options["異常類型/外觀"], [q["嚴重度"]] = q.Options["嚴重度/重大"]
        });
        var blocked = await Delete(IdOf(order));
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(blocked);
        body.GetProperty("message").GetString().Should().Be("還有其他資料參照這筆記錄，不能刪除");
        body.GetProperty("total").GetInt32().Should().Be(1);
    }
}
