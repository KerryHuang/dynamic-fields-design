using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynRecord = DynamicModules.Domain.Entities.Dynamic.Record;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>格式 3：套件帶狀態機、單據轉換、帶值與報表；匯出再匯入到空資料庫後行為仍在。</summary>
public class AppV3Tests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppV3Tests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static readonly object CreateGroups = new
    {
        groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["lead"] = new { mode = "create" } }
    };

    private static async Task<JsonElement> ImportAndOpen(HttpClient c, string json, string id, object? body = null)
    {
        var imported = await c.PostAsync($"{Apps}/packages/import", Body(json));
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        var opened = await c.PostAsJsonAsync($"{Apps}/packages/{id}/open", body ?? CreateGroups);
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        return await JsonOf(opened);
    }

    private static string KeyOf(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    /// <summary>欄位標籤等於欄位代號：用標籤找欄位 key。</summary>
    private static async Task<Dictionary<string, string>> FieldKeys(HttpClient c, string moduleKey)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!);
    }

    private static async Task<string> OptionValue(HttpClient c, string moduleKey, string fieldLabel, string optionLabel)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        var field = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == fieldLabel);
        return field.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == optionLabel).GetProperty("value").GetString()!;
    }

    private static async Task<JsonElement> PostOk(HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var res = await c.PostAsJsonAsync(url, body);
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string? Str(JsonElement record, string key) =>
        record.GetProperty("values").TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>走完整條單據流：帶值 → 狀態機擋 → 核准 → 唯讀 → 轉換 → 剩餘量。開啟後的應用程式與匯出再匯入的應用程式共用。</summary>
    private static async Task DriveSalesFlow(HttpClient admin, JsonElement opened)
    {
        string customer = KeyOf(opened, "customer"), quote = KeyOf(opened, "quote"), quoteLine = KeyOf(opened, "quote-line"),
            order = KeyOf(opened, "order"), orderLine = KeyOf(opened, "order-line");
        var cust = await FieldKeys(admin, customer);
        var q = await FieldKeys(admin, quote);
        var ql = await FieldKeys(admin, quoteLine);
        var o = await FieldKeys(admin, order);

        var c1 = await PostOk(admin, $"{Base}/modules/{customer}/records", new { values = new Dictionary<string, object> { [cust["name"]] = "甲公司" } });

        // 帶值：選客戶後，客戶名稱由系統填入（lock，使用者送的值不採用）
        var created = await PostOk(admin, $"{Base}/modules/{quote}/records", new
        {
            values = new Dictionary<string, object> { [q["customer"]] = c1.GetProperty("id").GetInt64(), [q["customerName"]] = "偽造" },
            details = new Dictionary<string, object[]>
            {
                [quoteLine] = [new { values = new Dictionary<string, object> { [ql["item"]] = "螺絲", [ql["qty"]] = 10 } }]
            }
        });
        Str(created, q["customerName"]).Should().Be("甲公司");
        var id = created.GetProperty("id").GetInt64();
        var lineId = created.GetProperty("details").GetProperty(quoteLine)[0].GetProperty("id").GetInt64();

        // 狀態機：草稿狀態不在轉換的來源狀態內
        var convertUrl = $"{Base}/records/{id}/conversions/sales-quote-to-order/convert";
        var early = await admin.PostAsJsonAsync(convertUrl, new { lines = new[] { new { sourceLineId = lineId, quantity = 4 } } });
        early.StatusCode.Should().NotBe(HttpStatusCode.Created);

        // 狀態機：沒定義的轉移（已核准 → 草稿）被擋；定義的轉移（草稿 → 已核准）放行
        var approved = await OptionValue(admin, quote, "status", "已核准");
        var draft = await OptionValue(admin, quote, "status", "草稿");
        var approve = await admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [q["status"]] = approved }, rowVersion = created.GetProperty("rowVersion").GetString()
        });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        var after = await JsonOf(approve);
        var back = await admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [q["status"]] = draft }, rowVersion = after.GetProperty("rowVersion").GetString()
        });
        back.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 唯讀狀態：其他欄位不能改（輸入被忽略，客戶名稱仍是原值）
        var edit = await admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [q["customerName"]] = "改了" }, rowVersion = after.GetProperty("rowVersion").GetString()
        });
        if (edit.IsSuccessStatusCode) Str(await JsonOf(edit), q["customerName"]).Should().Be("甲公司");

        // 轉換：部分轉 4 / 10，再超轉 7 被擋
        var converted = await PostOk(admin, convertUrl, new { lines = new[] { new { sourceLineId = lineId, quantity = 4 } } });
        Str(converted, o["customerName"]).Should().Be("甲公司");
        var over = await admin.PostAsJsonAsync(convertUrl, new { lines = new[] { new { sourceLineId = lineId, quantity = 7 } } });
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var status = await JsonOf(await admin.GetAsync($"{Base}/records/{id}/conversions"));
        status[0].GetProperty("lines")[0].GetProperty("remaining").GetDecimal().Should().Be(6);
        _ = orderLine;
    }

    private async Task<JsonElement> ExportOk(string key)
    {
        var res = await _admin.GetAsync($"{Apps}/{key}/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    [Fact]
    public async Task OpenV3_StateMachineFillConversionAndReport_AllTakeEffect()
    {
        var opened = await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        opened.GetProperty("formatVersion").GetInt32().Should().Be(3);
        await DriveSalesFlow(_admin, opened);

        Db(db => db.Set<Workflow>().Count()).Should().Be(1);
        Db(db => db.Set<Conversion>().Count(c => c.Key == "sales-quote-to-order")).Should().Be(1);
        Db(db => db.Set<AppReport>().Count()).Should().Be(1);
        var report = Db(db => db.Set<Report>().Include(r => r.Shares).Single());
        report.Name.Should().Be("報價狀態分布");
        report.Shares.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExportThenImportIntoEmptyDatabase_BehaviorSurvives()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var exported = await ExportOk("sales-flow");
        exported.GetProperty("formatVersion").GetInt32().Should().Be(5);
        exported.GetProperty("workflows").GetArrayLength().Should().Be(1);
        exported.GetProperty("conversions").GetArrayLength().Should().Be(1);
        exported.GetProperty("reports").GetArrayLength().Should().Be(1);
        exported.GetRawText().Should().Contain("\"fill\"");
        // 列印範本匯出時：迴圈換回模組代號，不留資料庫的模組鍵；欄位佔位符用匯出的欄位代號（開啟後欄位代號就是 f_<Id>），
        // 和同一份套件裡的欄位一致，由重新匯入後能列印來證明
        var exportedTemplate = exported.GetProperty("printTemplates").EnumerateArray().Single();
        exportedTemplate.GetProperty("module").GetString().Should().Be("quote");
        exportedTemplate.GetProperty("code").GetString().Should().Be("quote-sheet");
        var exportedBody = exportedTemplate.GetProperty("body").GetString()!;
        exportedBody.Should().Contain("{{#each quote-line}}").And.NotContain("sales-flow-");
        exportedBody.Should().StartWith("<h1>{{no}}</h1><p>{{customerName}}</p>");

        using var fresh = new TestApp();
        var admin2 = fresh.AsUser();
        var reopened = await ImportAndOpen(admin2, exported.GetRawText(), "sales-flow",
            new
            {
                groupMap = exported.GetProperty("groups").EnumerateArray()
                    .ToDictionary(g => g.GetProperty("code").GetString()!, g => (object)new { mode = "create" })
            });
        await DriveSalesFlow(admin2, reopened);

        // 重新匯入的應用程式：範本在，而且真的印得出來
        var quote = KeyOf(reopened, "quote");
        var line = KeyOf(reopened, "quote-line");
        var customer = KeyOf(reopened, "customer");
        var c = await PostOk(admin2, $"{Base}/modules/{customer}/records",
            new { values = new Dictionary<string, object> { [(await FieldKeys(admin2, customer))["name"]] = "乙公司" } });
        var q = await FieldKeys(admin2, quote);
        var ql = await FieldKeys(admin2, line);
        var created = await PostOk(admin2, $"{Base}/modules/{quote}/records", new
        {
            values = new Dictionary<string, object> { [q["customer"]] = c.GetProperty("id").GetInt64() },
            details = new Dictionary<string, object[]> { [line] = [new { values = new Dictionary<string, object> { [ql["item"]] = "墊片", [ql["qty"]] = 3 } }] }
        });
        var printed = await admin2.GetAsync($"{Base}/records/{created.GetProperty("id").GetInt64()}/print/quote-sheet");
        printed.StatusCode.Should().Be(HttpStatusCode.OK, await printed.Content.ReadAsStringAsync());
        var html = await printed.Content.ReadAsStringAsync();
        html.Should().Contain("<p>乙公司</p>").And.Contain("<td>墊片</td><td>3</td>");
    }

    [Fact]
    public async Task OpenWithPrintTemplate_StoresDatabaseKeysAndSanitizedBody()
    {
        var nasty = """[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "報價單" }, "active": true, "body": "<script>alert(1)</script><p onclick=\"x()\">{{no}}</p>" } ]""";
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(printTemplates: nasty), "sales-flow");
        var body = Db(db => db.Set<PrintTemplate>().Single().Body);
        body.Should().MatchRegex("^<p>\\{\\{f_\\d+\\}\\}</p>$");
    }

    [Theory]
    [InlineData("""[ { "module": "nope", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "<p>x</p>" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "Bad Code", "name": { "zh-TW": "x" }, "active": true, "body": "<p>x</p>" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "<p>{{noSuchField}}</p>" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "{{#each order-line}}x{{/each}}" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "{{#each quote-line}}{{noSuch}}{{/each}}" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "{{#each quote-line}}never closed" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "quote-sheet", "name": { "zh-TW": "x" }, "active": true, "body": "" } ]""")]
    [InlineData("""[ { "module": "quote", "code": "a-1", "name": { "zh-TW": "x" }, "active": true, "body": "<p>1</p>" }, { "module": "quote", "code": "a-1", "name": { "zh-TW": "y" }, "active": true, "body": "<p>2</p>" } ]""")]
    public async Task PackageWithBadPrintTemplate_IsRejected(string printTemplates)
    {
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV3.Sales(printTemplates: printTemplates)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("printTemplates");
    }

    [Fact]
    public async Task V2Package_WithPrintTemplates_IsRejected()
    {
        var json = AppTestPackagesV3.Sales(formatVersion: 2, workflows: "[]", conversions: "[]", reports: "[]", fill: "")
            .Replace("\"reports\"", "\"printTemplates\": [ { \"module\": \"quote\", \"code\": \"quote-sheet\", \"name\": { \"zh-TW\": \"x\" }, \"active\": true, \"body\": \"<p>x</p>\" } ], \"reports\"");
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(json));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("printTemplates");
    }

    [Fact]
    public async Task Delete_RemovesPrintTemplates()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        Db(db => db.Set<PrintTemplate>().Count()).Should().Be(1);
        (await _admin.PostAsJsonAsync($"{Apps}/sales-flow/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.DeleteAsync($"{Apps}/sales-flow?purged=0")).IsSuccessStatusCode.Should().BeTrue();
        Db(db => db.Set<PrintTemplate>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task V2Package_WithV3Sections_IsRejected()
    {
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV3.Sales(formatVersion: 2)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().Contain("workflows").And.Contain("conversions");
    }

    [Fact]
    public async Task V2Package_WithFill_IsRejected()
    {
        var json = AppTestPackagesV3.Sales(formatVersion: 2, workflows: "[]", conversions: "[]", reports: "[]");
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(json));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("fill");
    }

    [Theory]
    [InlineData("workflowOption", "\"initial\": \"draft\"", "\"initial\": \"nope\"")]
    [InlineData("workflowGroup", "\"groups\": [\"lead\"]", "\"groups\": [\"ghost\"]")]
    [InlineData("conversionField", "\"to\": \"order.customerName\"", "\"to\": \"order.ghost\"")]
    [InlineData("fillField", "\"from\": \"customer.name\"", "\"from\": \"customer.ghost\"")]
    [InlineData("reportField", "\"field\": \"quote.status\"", "\"field\": \"quote.ghost\"")]
    public async Task V3Package_WithUnknownReference_IsRejectedAtImport(string name, string find, string replace)
    {
        var json = AppTestPackagesV3.Sales().Replace(find, replace);
        json.Should().NotBe(AppTestPackagesV3.Sales(), name);
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(json));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, name + await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Open_WithInvalidConversionDefinition_RollsBackEverything()
    {
        // 追溯欄位沒設唯讀：結構上合法，但轉換定義的環境層驗證（SaveConversion）會擋，整批回滾
        var json = AppTestPackagesV3.Sales().Replace(
            """{ "code": "fromLine", "type": "integer", "label": { "zh-TW": "fromLine" }, "config": { "readOnly": true } }""",
            """{ "code": "fromLine", "type": "integer", "label": { "zh-TW": "fromLine" }, "config": {} }""");
        (await _admin.PostAsync($"{Apps}/packages/import", Body(json))).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/sales-flow/open", CreateGroups);
        res.IsSuccessStatusCode.Should().BeFalse();
        Db(db => db.Set<App>().Count()).Should().Be(0);
        Db(db => db.Set<Module>().Count(m => m.Key.StartsWith("sales-flow"))).Should().Be(0);
        Db(db => db.Set<Conversion>().Count()).Should().Be(0);
        Db(db => db.Set<Report>().Count()).Should().Be(0);
        Db(db => db.Set<PermissionGroup>().Count(g => g.Key.StartsWith("g-"))).Should().Be(0);
    }

    [Fact]
    public async Task Open_Twice_IsConflict_AndConversionCodeClashIsReported()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        (await _admin.PostAsJsonAsync($"{Apps}/packages/sales-flow/open", CreateGroups)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 另一個應用程式用了相同的轉換代碼：預覽就看得到錯誤，開啟不寫入
        var other = AppTestPackagesV3.Sales(appId: "sales-flow2");
        (await _admin.PostAsync($"{Apps}/packages/import", Body(other))).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/sales-flow2/open", CreateGroups);
        res.IsSuccessStatusCode.Should().BeFalse();
        Db(db => db.Set<App>().Count()).Should().Be(1);
    }

    [Fact]
    public async Task Closed_BlocksConversionAndTransition_ReopenRestores()
    {
        var opened = await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        string customer = KeyOf(opened, "customer"), quote = KeyOf(opened, "quote"), quoteLine = KeyOf(opened, "quote-line");
        var cust = await FieldKeys(_admin, customer);
        var q = await FieldKeys(_admin, quote);
        var ql = await FieldKeys(_admin, quoteLine);
        var c1 = await PostOk(_admin, $"{Base}/modules/{customer}/records", new { values = new Dictionary<string, object> { [cust["name"]] = "乙" } });
        var created = await PostOk(_admin, $"{Base}/modules/{quote}/records", new
        {
            values = new Dictionary<string, object> { [q["customer"]] = c1.GetProperty("id").GetInt64() },
            details = new Dictionary<string, object[]>
            {
                [quoteLine] = [new { values = new Dictionary<string, object> { [ql["item"]] = "墊片", [ql["qty"]] = 5 } }]
            }
        });
        var id = created.GetProperty("id").GetInt64();
        var lineId = created.GetProperty("details").GetProperty(quoteLine)[0].GetProperty("id").GetInt64();
        var approved = await OptionValue(_admin, quote, "status", "已核准");
        var approve = await _admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [q["status"]] = approved }, rowVersion = created.GetProperty("rowVersion").GetString()
        });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _admin.PostAsJsonAsync($"{Apps}/sales-flow/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        var convertUrl = $"{Base}/records/{id}/conversions/sales-quote-to-order/convert";
        var blocked = await _admin.PostAsJsonAsync(convertUrl, new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } });
        blocked.IsSuccessStatusCode.Should().BeFalse();
        var orderId = opened.GetProperty("modules").EnumerateArray()
            .Single(m => m.GetProperty("code").GetString() == "order").GetProperty("id").GetInt64();
        Db(db => db.Set<DynRecord>().Count(r => r.ModuleId == orderId)).Should().Be(0);
        (await _admin.GetAsync($"{Base}/records/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _admin.PostAsync($"{Apps}/sales-flow/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var ok = await _admin.PostAsJsonAsync(convertUrl, new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } });
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_RemovesReportsAndConversions()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        (await _admin.PostAsJsonAsync($"{Apps}/sales-flow/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await _admin.DeleteAsync($"{Apps}/sales-flow?purged=0");
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        Db(db => db.Set<Report>().Count()).Should().Be(0);
        Db(db => db.Set<Conversion>().Count()).Should().Be(0);
        Db(db => db.Set<Workflow>().Count()).Should().Be(0);
        Db(db => db.Set<AppReport>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_AppReportOnADashboardCard_IsBlocked_AndSucceedsOnceTheCardIsGone()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var reportId = Db(db => db.Set<AppReport>().Select(a => a.ReportId).First());
        // 卡片不設 SortKey（不指向任何欄位），只引用報表；儀表板是指派版（不屬於刪除者）
        var dashboard = await DashboardTestKit.CreateAssigned(_admin, "業務看板",
            DashboardTestKit.Layout(DashboardTestKit.Card("a", reportId)), Db(db => db.Set<PermissionGroup>().First().Id));
        (await _admin.PostAsJsonAsync($"{Apps}/sales-flow/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await _admin.DeleteAsync($"{Apps}/sales-flow?purged=0");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict, await blocked.Content.ReadAsStringAsync());
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("業務看板");
        Db(db => db.Set<Report>().Count()).Should().BeGreaterThan(0);

        (await _admin.DeleteAsync($"{DashboardTestKit.AdminDashboards}/{dashboard}")).EnsureSuccessStatusCode();
        var ok = await _admin.DeleteAsync($"{Apps}/sales-flow?purged=0");
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync());
    }

    // ---- 報表條件的編碼（fix-1 #5 #6 #7）----

    private static string ReportWithFilter(string filterJson) => $$"""
        [ { "code": "r1", "name": "測試報表", "shareWith": [],
            "definition": { "version": 1, "mainModule": "quote", "child": null, "columns": [],
              "groups": [ { "field": { "source": "main", "field": "quote.status", "via": null }, "bucket": null } ],
              "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
              "filters": [ {{filterJson}} ], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" } } ]
        """;

    [Fact]
    public async Task Import_TextFieldFilterValueThatLooksLikeAnOption_IsNotRewritten()
    {
        var filter = """{ "field": { "source": "main", "field": "quote.customerName", "via": null }, "op": "eq", "value": "opt_approved" }""";
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(reports: ReportWithFilter(filter)), "sales-flow");
        var definition = Db(db => db.Set<Report>().Select(r => r.DefinitionJson).Single());
        definition.Should().Contain("\"opt_approved\"").And.NotMatchRegex("opt_\\d");
    }

    [Fact]
    public async Task Import_EnumFieldFilterValue_IsStillMappedToTheOptionId()
    {
        var filter = """{ "field": { "source": "main", "field": "quote.status", "via": null }, "op": "eq", "value": "opt_approved" }""";
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(reports: ReportWithFilter(filter)), "sales-flow");
        Db(db => db.Set<Report>().Select(r => r.DefinitionJson).Single()).Should().MatchRegex("\"opt_\\d+\"");
    }

    [Theory]
    [InlineData("eq", "123")]
    [InlineData("in", "[1, 2]")]
    public async Task Import_ReportConditionOnAConcreteRecord_IsRejected(string op, string value)
    {
        var filter = $$"""{ "field": { "source": "main", "field": "quote.customer", "via": null }, "op": "{{op}}", "value": {{value}} }""";
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV3.Sales(reports: ReportWithFilter(filter))));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("具體記錄");
    }

    [Fact]
    public async Task Import_ReferenceFilterWithoutAValue_IsAccepted()
    {
        var filter = """{ "field": { "source": "main", "field": "quote.customer", "via": null }, "op": "isNotEmpty", "value": null }""";
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(reports: ReportWithFilter(filter)), "sales-flow");
    }

    [Fact]
    public async Task Export_ReportConditionOnAConcreteRecord_IsNotExported_AndTheUserIsTold()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var quote = Db(db => db.Set<Module>().Include(m => m.Blocks).ThenInclude(b => b.Fields).Single(m => m.Key == "sales-flow-quote"));
        var customerField = quote.Blocks.SelectMany(b => b.Fields).Single(f => f.Type == FieldType.Reference).Id;
        Db(db =>
        {
            var report = db.Set<Report>().Single();
            var node = System.Text.Json.Nodes.JsonNode.Parse(report.DefinitionJson)!;
            node["filters"] = System.Text.Json.Nodes.JsonNode.Parse(
                $$"""[ { "field": { "source": "main", "fieldId": {{customerField}}, "viaFieldId": null }, "op": "eq", "value": 123 } ]""");
            report.DefinitionJson = node.ToJsonString();
            db.SaveChanges();
            return 0;
        });

        var res = await _admin.GetAsync($"{Apps}/sales-flow/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var warnings = Uri.UnescapeDataString(string.Join('|', res.Headers.GetValues("X-Export-Warnings")));
        warnings.Should().Contain("報價狀態分布").And.Contain("具體記錄");
        (await JsonOf(res)).GetProperty("reports").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Export_OptionScopeOfADeactivatedEnumField_IsDropped_SoTheFileCanBeReimported()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        // 一個沒有被規則或狀態機引用的普通 enum 欄位，其中一個選項設了群組範圍，然後欄位被停用
        var field = await AddField(_admin, "sales-flow-customer", "enum");
        var optionRes = await _admin.PostAsJsonAsync($"{Base}/fields/{field.Id}/options",
            new { label = new Dictionary<string, string> { ["zh-TW"] = "外包" } });
        optionRes.EnsureSuccessStatusCode();
        var option = (await JsonOf(optionRes)).GetProperty("id").GetInt64();
        var groupId = Db(db => db.Set<PermissionGroup>().First().Id);
        (await _admin.PutAsJsonAsync($"{Base}/modules/sales-flow-customer/permissions", new
        {
            groups = Array.Empty<object>(), users = Array.Empty<object>(), fields = Array.Empty<object>(),
            options = new[] { new { optionId = option, groupIds = new[] { groupId } } }
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        Db(db => db.Set<FieldOptionScope>().Count()).Should().Be(1);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{field.Id}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var exported = await ExportOk("sales-flow");
        exported.GetProperty("optionScopes").GetArrayLength().Should().Be(0);

        using var fresh = new TestApp();
        await ImportAndOpen(fresh.AsUser(), exported.GetRawText(), "sales-flow", new { });
    }

    [Fact]
    public async Task Export_CrossAppReference_BecomesRequiresAndAppTarget_AndRoundTrips()
    {
        await ImportAndOpen(_admin, AppTestPackages.Crm, "crm-lite",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } } });
        await ImportAndOpen(_admin, AppTestPackages.Quote, "quote-lite",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "skip" } } });

        var exported = await ExportOk("quote-lite");
        var requires = exported.GetProperty("manifest").GetProperty("requires").GetProperty("apps");
        requires.GetArrayLength().Should().Be(1);
        requires[0].GetProperty("id").GetString().Should().Be("crm-lite");
        requires[0].GetProperty("modules")[0].GetString().Should().Be("customer");
        System.Text.RegularExpressions.Regex.IsMatch(exported.GetRawText(), "\"app\":\\s*\"crm-lite\"").Should().BeTrue();

        // 往返：另一個空資料庫先開 crm-lite，再開匯出的 quote-lite
        using var fresh = new TestApp();
        var admin2 = fresh.AsUser();
        await ImportAndOpen(admin2, AppTestPackages.Crm, "crm-lite",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } } });
        var reopened = await ImportAndOpen(admin2, exported.GetRawText(), "quote-lite", new { });
        reopened.GetProperty("modules").GetArrayLength().Should().Be(1);
    }
    // ---- 套件邊界（階段 2 前小修包）：報表條件不得帶資料庫專屬的值，兩個套件之間的引用要明確失敗 ----

    /// <summary>在已開啟的 sales-flow 報表上換掉篩選（直接寫定義 JSON，模擬使用者在畫面上存的條件）。</summary>
    private void SetReportFilters(string filtersJson) => Db(db =>
    {
        var report = db.Set<Report>().Single();
        var node = System.Text.Json.Nodes.JsonNode.Parse(report.DefinitionJson)!;
        node["filters"] = System.Text.Json.Nodes.JsonNode.Parse(filtersJson);
        report.DefinitionJson = node.ToJsonString();
        db.SaveChanges();
        return 0;
    });

    [Theory]
    [InlineData("eq", "\"sales01\"")]
    [InlineData("in", "[\"sales01\", \"sales02\"]")]
    public async Task Export_ReportConditionOnAUser_IsNotExported_BecauseAccountsBelongToThisDatabase(string op, string value)
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var owner = await AddField(_admin, "sales-flow-quote", "user");
        SetReportFilters($$"""[ { "field": { "source": "main", "fieldId": {{owner.Id}}, "viaFieldId": null }, "op": "{{op}}", "value": {{value}} } ]""");

        var res = await _admin.GetAsync($"{Apps}/sales-flow/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var warnings = Uri.UnescapeDataString(string.Join('|', res.Headers.GetValues("X-Export-Warnings")));
        warnings.Should().Contain("報價狀態分布").And.Contain("具體");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("sales01");
        System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("reports").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Export_ReportConditionOnAUser_WithoutAValue_IsStillExported()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var owner = await AddField(_admin, "sales-flow-quote", "user");
        SetReportFilters($$"""[ { "field": { "source": "main", "fieldId": {{owner.Id}}, "viaFieldId": null }, "op": "isEmpty", "value": null } ]""");

        (await ExportOk("sales-flow")).GetProperty("reports").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Import_ReportConditionOnAUser_IsRejected()
    {
        var package = AppTestPackagesV3.Sales(reports: ReportWithFilter(
            """{ "field": { "source": "main", "field": "quote.owner", "via": null }, "op": "eq", "value": "sales01" }"""))
            .Replace("""{ "code": "customerName", "type": "text", "label": { "zh-TW": "customerName" }, "config": { "readOnly": true } },""",
                     """{ "code": "customerName", "type": "text", "label": { "zh-TW": "customerName" }, "config": { "readOnly": true } }, { "code": "owner", "type": "user", "label": { "zh-TW": "owner" }, "config": {} },""");

        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(package));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("具體");
    }

    [Fact]
    public async Task Import_ReportReferencingAModuleOfAnotherPackage_IsRejected_NotGuessed()
    {
        // 套件裡的報表只能引用套件自己的模組；別的應用程式的模組與欄位（含它們的選項、參照型欄位）解不了，要明確失敗
        var filter = """{ "field": { "source": "main", "field": "customer-of-another-app.grade", "via": null }, "op": "eq", "value": "opt_gold" }""";
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV3.Sales(reports: ReportWithFilter(filter))));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("customer-of-another-app.grade");
    }

    [Fact]
    public async Task Export_ReportReferencingAnotherAppsField_IsNotExported_AndTheUserIsTold()
    {
        await ImportAndOpen(_admin, AppTestPackages.Crm, "crm-lite",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } } });
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var crmCustomerStatus = Db(db => db.Set<Module>().Include(m => m.Blocks).ThenInclude(b => b.Fields)
            .Single(m => m.Key == "crm-customer").Blocks.SelectMany(b => b.Fields).First(f => f.Type == FieldType.Enum).Id);
        SetReportFilters($$"""[ { "field": { "source": "main", "fieldId": {{crmCustomerStatus}}, "viaFieldId": null }, "op": "eq", "value": "opt_1" } ]""");

        var res = await _admin.GetAsync($"{Apps}/sales-flow/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        Uri.UnescapeDataString(string.Join('|', res.Headers.GetValues("X-Export-Warnings"))).Should().Contain("報價狀態分布");
        (await JsonOf(res)).GetProperty("reports").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Export_OptionScopeWhoseGroupsAreGone_IsDropped_SoTheFileCanBeReimported()
    {
        await ImportAndOpen(_admin, AppTestPackagesV3.Sales(), "sales-flow");
        var field = await AddField(_admin, "sales-flow-customer", "enum");
        var optionRes = await _admin.PostAsJsonAsync($"{Base}/fields/{field.Id}/options",
            new { label = new Dictionary<string, string> { ["zh-TW"] = "外包" } });
        optionRes.EnsureSuccessStatusCode();
        var option = (await JsonOf(optionRes)).GetProperty("id").GetInt64();
        // 範圍指向一個已不存在的群組（資料庫直接寫入，模擬殘留資料）
        Db(db =>
        {
            db.Set<FieldOptionScope>().Add(new FieldOptionScope { OptionId = option, GroupId = 987_654 });
            db.SaveChanges();
            return 0;
        });

        var exported = await ExportOk("sales-flow");
        exported.GetProperty("optionScopes").GetArrayLength().Should().Be(0);

        using var fresh = new TestApp();
        await ImportAndOpen(fresh.AsUser(), exported.GetRawText(), "sales-flow", new { });
    }
}
