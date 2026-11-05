using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>內建範本之外，再加三個測試專用範本：規則條件值不合法、權限群組不存在、套用到一半丟例外。</summary>
public sealed class TemplateTestApp : TestApp
{
    public const string BadRuleTemplate = """
        { "formatVersion": 1, "key": "bad-rule", "name": { "zh-TW": "壞規則" },
          "modules": [ { "code": "x", "key": "bad-x", "name": { "zh-TW": "壞" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "標題" } },
              { "code": "qty", "type": "integer", "label": { "zh-TW": "數量" } } ] } ],
            "rules": [ { "kind": "readOnly", "targets": ["*"], "active": true,
                         "condition": { "field": "qty", "op": "eq", "value": "abc" } } ] } ] }
        """;

    public const string UnknownGroupTemplate = """
        { "formatVersion": 1, "key": "nope-group", "name": { "zh-TW": "未知群組" },
          "modules": [ { "code": "g", "key": "nope-g", "name": { "zh-TW": "群組測試" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "標題" } } ] } ],
            "permissions": [
              { "group": "nope", "scope": "all", "privileges": { "read": true, "add": false, "edit": false, "delete": false,
                "printExport": false, "pricing": false, "approve": false } },
              { "group": "sales", "scope": "own", "privileges": { "read": true, "add": true, "edit": false, "delete": false,
                "printExport": false, "pricing": false, "approve": false } } ] } ] }
        """;

    /// <summary>
    /// 規則的選項限制引用不存在的選項：沒經過 TemplateValidator（直接塞進 TemplateCatalog），
    /// 套用到第 8 步換選項 id 時丟 KeyNotFoundException；這時模組、欄位都已寫入。
    /// </summary>
    public const string ThrowingTemplate = """
        { "formatVersion": 1, "key": "throws", "name": { "zh-TW": "會丟例外" },
          "modules": [ { "code": "t", "key": "throws-t", "name": { "zh-TW": "例外" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "標題" } },
              { "code": "kind", "type": "enum", "label": { "zh-TW": "種類" }, "options": [ { "code": "a", "label": { "zh-TW": "甲" } } ] } ] } ],
            "rules": [ { "kind": "optionFilter", "targets": ["field:kind"], "options": ["nope"], "active": true } ] } ] }
        """;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            var packages = TemplateCatalog.Load(TemplateCatalog.DefaultDirectory).BuiltIns.ToList();
            packages.Add(TemplateJson.Parse(BadRuleTemplate).Package!);
            packages.Add(TemplateJson.Parse(UnknownGroupTemplate).Package!);
            packages.Add(TemplateJson.Parse(ThrowingTemplate).Package!);
            s.RemoveAll<TemplateCatalog>();
            s.AddSingleton(new TemplateCatalog(packages));
        });
    }
}

/// <summary>每個測試一個新的資料庫：內建範本的模組代碼固定，共用會互相撞。</summary>
public sealed class TemplateInstantiateTests : IDisposable
{
    private readonly TemplateTestApp _app = new();
    private readonly HttpClient _admin;

    public TemplateInstantiateTests() => _admin = _app.AsUser("admin");

    public void Dispose() => _app.Dispose();

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private Task<HttpResponseMessage> Instantiate(string key, object? modules = null, string[]? exclude = null, HttpClient? c = null) =>
        (c ?? _admin).PostAsJsonAsync($"{Base}/templates/{key}/instantiate",
            new { modules = modules ?? Array.Empty<object>(), excludeFields = exclude ?? [] });

    private async Task<JsonElement> Module(string key)
    {
        var res = await _admin.GetAsync($"{Base}/modules/{key}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, key);
        return await JsonOf(res);
    }

    private static IEnumerable<JsonElement> Fields(JsonElement module) =>
        module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray());

    private static JsonElement Field(JsonElement module, string label) =>
        Fields(module).Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == label);

    private static long Id(JsonElement e) => e.GetProperty("id").GetInt64();

    private async Task<int> ModuleCount() => (await JsonOf(await _admin.GetAsync($"{Base}/modules"))).GetArrayLength();

    private async Task<JsonElement> Rules(string key) => await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/rules"));

    private static string FirstError(JsonElement body, string key) =>
        body.GetProperty("errors").GetProperty(key)[0].GetString()!;

    [Fact]
    public async Task GetTemplates_ListsOrder_WithLockedAndDependsOn()
    {
        var res = await _admin.GetAsync($"{Base}/templates");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await JsonOf(res);
        var order = list.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "order");
        order.GetProperty("source").GetString().Should().Be("builtIn");
        order.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("訂單");

        var modules = order.GetProperty("modules");
        var head = modules[0];
        head.GetProperty("code").GetString().Should().Be("order");
        head.TryGetProperty("detailOf", out _).Should().BeFalse();
        var line = modules[1];
        line.GetProperty("key").GetString().Should().Be("order-line");
        line.GetProperty("detailOf").GetString().Should().Be("order");

        JsonElement F(JsonElement m, string code) =>
            m.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("code").GetString() == code);
        List<string> Deps(JsonElement f) => f.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()!).ToList();

        F(head, "no").GetProperty("locked").GetBoolean().Should().BeTrue();
        F(head, "no").GetProperty("type").GetString().Should().Be("autonumber");
        F(head, "remark").GetProperty("locked").GetBoolean().Should().BeFalse();
        F(line, "order").GetProperty("locked").GetBoolean().Should().BeTrue();
        Deps(F(head, "tax")).Should().Equal("order.subtotal", "order.taxRate");
        Deps(F(head, "subtotal")).Should().Equal("line.order", "line.amount");
        Deps(F(line, "dueDate")).Should().Equal("order.dueDate");
        F(head, "subtotal").GetProperty("label").GetProperty("zh-TW").GetString().Should().Be("未稅合計");
    }

    [Fact]
    public async Task Instantiate_Order_CreatesDraftModules()
    {
        var res = await Instantiate("order");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var created = body.GetProperty("modules");
        created.EnumerateArray().Select(m => (m.GetProperty("code").GetString(), m.GetProperty("key").GetString()))
            .Should().Equal(("order", "order"), ("line", "order-line"));

        var order = await Module("order");
        var line = await Module("order-line");
        Id(order).Should().Be(created[0].GetProperty("id").GetInt64());
        order.GetProperty("status").GetString().Should().Be("Draft");
        line.GetProperty("status").GetString().Should().Be("Draft");
        order.GetProperty("icon").GetString().Should().Be("receipt_long");
        order.GetProperty("section").GetString().Should().Be("業務");

        order.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("label").GetProperty("zh-TW").GetString())
            .Should().Equal("基本資料", "金額", "備註");
        order.GetProperty("blocks").EnumerateArray()
            .Select(b => string.Join(",", b.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("label").GetProperty("zh-TW").GetString())))
            .Should().Equal("訂單編號,客戶,訂單日期,交期,狀態,業務員,幣別", "未稅合計,稅率,稅額,含稅總額,明細筆數", "備註,取消原因");
        Fields(line).Select(f => f.GetProperty("label").GetProperty("zh-TW").GetString())
            .Should().Equal("訂單", "項次", "品號", "品名", "數量", "單位", "單價", "金額", "交期");
        line.GetProperty("blocks")[0].GetProperty("label").GetProperty("zh-TW").GetString().Should().Be("明細");

        order.GetProperty("titleFieldId").GetInt64().Should().Be(Id(Field(order, "訂單編號")));
        line.GetProperty("titleFieldId").GetInt64().Should().Be(Id(Field(line, "品名")));
        line.GetProperty("detailOf").GetString().Should().Be("order");
        Field(line, "訂單").GetProperty("config").GetProperty("relation").GetString().Should().Be("detail");

        var rollup = Field(order, "未稅合計").GetProperty("config").GetProperty("rollup");
        rollup.GetProperty("sourceFieldId").GetInt64().Should().Be(Id(Field(line, "訂單")));
        rollup.GetProperty("aggregateFieldId").GetInt64().Should().Be(Id(Field(line, "金額")));
        Field(order, "稅額").GetProperty("config").GetProperty("formula").GetString()
            .Should().Be($"ROUND({{{Id(Field(order, "未稅合計"))}}} * {{{Id(Field(order, "稅率"))}}}, 0)");
        var status = Field(order, "狀態");
        status.GetProperty("options").GetArrayLength().Should().Be(5);
        var draftOpt = status.GetProperty("options")[0].GetProperty("value").GetString();
        status.GetProperty("config").GetProperty("defaultValue").GetString().Should().Be(draftOpt);
        Field(order, "訂單日期").GetProperty("config").GetProperty("defaultToday").GetBoolean().Should().BeTrue();
        Field(line, "交期").GetProperty("config").GetProperty("parentDefaultFieldId").GetInt64().Should().Be(Id(Field(order, "交期")));

        var rules = await Rules("order");
        rules.GetArrayLength().Should().Be(4);
        var cancelOpt = status.GetProperty("options")[4].GetProperty("value").GetString();
        rules[1].GetProperty("condition").GetProperty("value").GetString().Should().Be(cancelOpt);
        rules[1].GetProperty("targets")[0].GetString().Should().Be($"field:{Id(Field(order, "取消原因"))}");

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var views = await db.Set<ListView>().AsNoTracking().Where(v => v.ModuleId == Id(order)).ToListAsync();
            views.Should().ContainSingle();
            views[0].IsDefault.Should().BeTrue();
            views[0].OwnerUserId.Should().BeNull();
            views[0].NameI18n.Resolve("zh-TW").Should().Be("全部訂單");
            views[0].Columns.Should().Equal(new[] { "訂單編號", "客戶", "訂單日期", "交期", "狀態", "含稅總額" }.Select(l => Id(Field(order, l))));
            views[0].Sort.Should().ContainSingle().Which.Should().Be(new Domain.ValueObjects.ViewSort(Id(Field(order, "訂單日期")), "desc"));
        }

        var groups = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .ToDictionary(g => g.GetProperty("id").GetInt64(), g => g.GetProperty("key").GetString()!);
        // GET 列出所有群組；範本沒給的群組權限全是 false
        var perms = (await JsonOf(await _admin.GetAsync($"{Base}/modules/order/permissions"))).GetProperty("groups").EnumerateArray()
            .Where(g => g.GetProperty("privileges").GetProperty("read").GetBoolean())
            .ToDictionary(g => groups[g.GetProperty("groupId").GetInt64()]);
        perms.Keys.Should().BeEquivalentTo("sales", "sales-manager", "floor");
        var sales = perms["sales"];
        sales.GetProperty("scope").GetString().Should().Be("own");
        var sp = sales.GetProperty("privileges");
        (sp.GetProperty("read").GetBoolean(), sp.GetProperty("add").GetBoolean(), sp.GetProperty("edit").GetBoolean(),
            sp.GetProperty("delete").GetBoolean(), sp.GetProperty("pricing").GetBoolean()).Should().Be((true, true, true, false, true));
        // R10：沒有金額軸的示範是現場人員（只讀、資料範圍全部）
        var floor = perms["floor"];
        floor.GetProperty("scope").GetString().Should().Be("all");
        var fp = floor.GetProperty("privileges");
        (fp.GetProperty("read").GetBoolean(), fp.GetProperty("add").GetBoolean(), fp.GetProperty("edit").GetBoolean(),
            fp.GetProperty("delete").GetBoolean(), fp.GetProperty("printExport").GetBoolean(), fp.GetProperty("pricing").GetBoolean())
            .Should().Be((true, false, false, false, false, false));
        var manager = perms["sales-manager"];
        manager.GetProperty("scope").GetString().Should().Be("all");
        var mp = manager.GetProperty("privileges");
        (mp.GetProperty("delete").GetBoolean(), mp.GetProperty("printExport").GetBoolean(), mp.GetProperty("pricing").GetBoolean(),
            mp.GetProperty("approve").GetBoolean()).Should().Be((true, true, true, false));
    }

    [Fact]
    public async Task Instantiate_Order_ThenActivateBoth_Succeeds()
    {
        (await Instantiate("order")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.PostAsync($"{Base}/modules/order/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Base}/modules/order-line/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var views = await JsonOf(await _admin.GetAsync($"{Base}/modules/order/views"));
        views.GetArrayLength().Should().Be(1);
        views[0].GetProperty("staleFieldIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Instantiate_Twice_WithNewKeys_IsIndependent()
    {
        (await Instantiate("order")).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await Instantiate("order", new object[]
        {
            new { code = "order", key = "order2" }, new { code = "line", key = "order2-line" }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());

        var order2 = await Module("order2");
        var line2 = await Module("order2-line");
        line2.GetProperty("detailOf").GetString().Should().Be("order2");
        Field(line2, "訂單").GetProperty("config").GetProperty("refTarget").GetProperty("key").GetString().Should().Be("order2");
        Field(order2, "未稅合計").GetProperty("config").GetProperty("rollup").GetProperty("sourceFieldId").GetInt64()
            .Should().Be(Id(Field(line2, "訂單")));
        Field(line2, "交期").GetProperty("config").GetProperty("parentDefaultFieldId").GetInt64().Should().Be(Id(Field(order2, "交期")));
        (await Module("order-line")).GetProperty("detailOf").GetString().Should().Be("order");
    }

    [Fact]
    public async Task Instantiate_KeyTaken_409_LeavesNothing()
    {
        var manual = await _admin.PostAsJsonAsync($"{Base}/modules", new { key = "order-line", name = Zh("手動") });
        manual.StatusCode.Should().Be(HttpStatusCode.Created);
        var code = (await JsonOf(manual)).GetProperty("programCode").GetString()!;
        var count = await ModuleCount();

        var res = await Instantiate("order");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("模組「line」：模組代碼已存在");

        (await _admin.GetAsync($"{Base}/modules/order")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ModuleCount()).Should().Be(count);
        // 失敗的那次本來會把下一個程式代碼給 order；回滾後下一個模組拿到同一個代碼
        var next = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules", new { key = NewKey(), name = Zh("下一個") }));
        next.GetProperty("programCode").GetString().Should().Be($"{code[..^5]}{int.Parse(code[^5..]) + 1:D5}");
    }

    [Fact]
    public async Task Instantiate_FailureMidway_LeavesNothing()
    {
        var count = await ModuleCount();
        var res = await Instantiate("bad-rule");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        errors.Should().ContainSingle().Which.Should().StartWith("x.rules.");
        (await ModuleCount()).Should().Be(count);
        (await _admin.GetAsync($"{Base}/modules/bad-x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Instantiate_InnerThrows_LeavesNothing()
    {
        var before = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules", new { key = NewKey(), name = Zh("之前") }));
        var code = before.GetProperty("programCode").GetString()!;
        var count = await ModuleCount();

        var res = await Instantiate("throws");
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await _admin.GetAsync($"{Base}/modules/throws-t")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ModuleCount()).Should().Be(count);
        // 例外路徑也整個回滾：程式代碼沒有被用掉
        var next = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules", new { key = NewKey(), name = Zh("之後") }));
        next.GetProperty("programCode").GetString().Should().Be($"{code[..^5]}{int.Parse(code[^5..]) + 1:D5}");
    }

    [Fact]
    public async Task Instantiate_Exclude_CancelReason_DropsTwoRules()
    {
        var res = await Instantiate("order", exclude: ["order.cancelReason"]);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var order = await Module("order");
        Fields(order).Should().NotContain(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "取消原因");
        var rules = await Rules("order");
        rules.EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Equal("validation", "readOnly");
    }

    [Fact]
    public async Task Instantiate_Exclude_TaxRateButKeepTax_400()
    {
        var count = await ModuleCount();
        var res = await Instantiate("order", exclude: ["order.taxRate"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        FirstError(await JsonOf(res), "excludeFields").Should().Be("「稅額」引用了「稅率」，要一起排除");
        (await ModuleCount()).Should().Be(count);
    }

    [Fact]
    public async Task Instantiate_Exclude_Locked_400()
    {
        var res = await Instantiate("order", exclude: ["order.no"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        FirstError(await JsonOf(res), "excludeFields").Should().Be("「訂單編號」是必要欄位，不能排除");
    }

    [Fact]
    public async Task Instantiate_UnknownGroup_Warns()
    {
        var res = await Instantiate("nope-group");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var warnings = (await JsonOf(res)).GetProperty("warnings");
        warnings.EnumerateArray().Select(w => w.GetString()).Should().Equal("群組「nope」不存在，已略過它的權限建議");
        var perms = (await JsonOf(await _admin.GetAsync($"{Base}/modules/nope-g/permissions"))).GetProperty("groups");
        perms.EnumerateArray().Count(g => g.GetProperty("privileges").GetProperty("read").GetBoolean()).Should().Be(1);
    }

    [Fact]
    public async Task Instantiate_NonDesigner_403()
    {
        (await Instantiate("order", c: _app.AsUser("sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync($"{Base}/modules/order")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Instantiate_Rename_UsesGivenNameAndKey()
    {
        var res = await Instantiate("order", new object[] { new { code = "order", key = "so", name = Zh("銷售單") } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("modules")[0].GetProperty("key").GetString().Should().Be("so");

        var so = await Module("so");
        so.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("銷售單");
        var line = await Module("order-line");
        line.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("訂單明細");
        line.GetProperty("detailOf").GetString().Should().Be("so");
    }

    [Fact]
    public async Task Instantiate_NullModuleCode_400()
    {
        var res = await Instantiate("order", new object[] { new { code = (string?)null, key = "so" } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        FirstError(await JsonOf(res), "Modules[0].Code").Should().Be("模組代號不能空白");
    }

    [Fact]
    public async Task Instantiate_DuplicateModuleCode_400()
    {
        var res = await Instantiate("order", new object[] { new { code = "order", key = "so" }, new { code = "order", key = "so2" } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        FirstError(await JsonOf(res), "Modules").Should().Be("模組代號不能重複");
    }

    [Fact]
    public async Task Instantiate_NullExcludeEntry_400()
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/templates/order/instantiate",
            new { modules = Array.Empty<object>(), excludeFields = new string?[] { null } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        FirstError(await JsonOf(res), "ExcludeFields[0]").Should().Be("要排除的欄位不能空白");
    }

    [Fact]
    public async Task Instantiate_UnknownTemplate_404()
    {
        var res = await Instantiate("nope");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("範本不存在");
    }
}
