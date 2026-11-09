using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>格式 4：套件帶連動事件、跨記錄檢查、分錄；開啟後行為在，匯出再匯入到空資料庫後行為仍在。</summary>
public class AppV4Tests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppV4Tests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static readonly object CreateLead = new { groupMap = new Dictionary<string, object> { ["lead"] = new { mode = "create" } } };

    private static async Task<JsonElement> ImportAndOpen(HttpClient c, string json, string id, object? body = null)
    {
        var imported = await c.PostAsync($"{Apps}/packages/import", Body(json));
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        var opened = await c.PostAsJsonAsync($"{Apps}/packages/{id}/open", body ?? CreateLead);
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        return await JsonOf(opened);
    }

    private static string KeyOf(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private static async Task<Dictionary<string, string>> FieldKeys(HttpClient c, string moduleKey)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!);
    }

    private static async Task<string> DoneValue(HttpClient c, string docKey)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{docKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "status").GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == "完成").GetProperty("value").GetString()!;
    }

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private async Task<JsonElement> ExportOk(string key)
    {
        var res = await _admin.GetAsync($"{Apps}/{key}/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    /// <summary>期初 +10；單據 5 件轉完成 → 異動 −5；再一張 7 件被檢查擋下且整筆回滾；異動只能沖銷。</summary>
    private static async Task DriveFlow(HttpClient admin, JsonElement opened)
    {
        string doc = KeyOf(opened, "doc"), line = KeyOf(opened, "doc-line"), move = KeyOf(opened, "move");
        var d = await FieldKeys(admin, doc);
        var dl = await FieldKeys(admin, line);
        var m = await FieldKeys(admin, move);
        var done = await DoneValue(admin, doc);

        var opening = await admin.PostAsJsonAsync($"{Base}/modules/{move}/records",
            new { values = new Dictionary<string, object> { [m["item"]] = "A", [m["qty"]] = 10 } });
        opening.StatusCode.Should().Be(HttpStatusCode.Created, await opening.Content.ReadAsStringAsync());
        var openingId = (await JsonOf(opening)).GetProperty("id").GetInt64();

        async Task<HttpResponseMessage> PostDoc(int qty)
        {
            var created = await admin.PostAsJsonAsync($"{Base}/modules/{doc}/records", new
            {
                values = new Dictionary<string, object>(),
                details = new Dictionary<string, object[]>
                {
                    [line] = [new { values = new Dictionary<string, object> { [dl["item"]] = "A", [dl["qty"]] = qty } }]
                }
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var body = await JsonOf(created);
            return await admin.PutAsJsonAsync($"{Base}/records/{body.GetProperty("id").GetInt64()}", new
            {
                values = new Dictionary<string, object> { [d["status"]] = done }, rowVersion = body.GetProperty("rowVersion").GetString()
            });
        }
        async Task<int> Count() =>
            (await JsonOf(await admin.GetAsync($"{Base}/modules/{move}/records/paged?currentPage=1&pagesSize=50"))).GetProperty("totalCount").GetInt32();

        (await PostDoc(5)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Count()).Should().Be(2);

        var over = await PostDoc(7);
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await over.Content.ReadAsStringAsync()).Should().Contain("庫存不足");
        (await Count()).Should().Be(2);

        var edit = await admin.PutAsJsonAsync($"{Base}/records/{openingId}", new
        {
            values = new Dictionary<string, object> { [m["qty"]] = 99 }, rowVersion = (await JsonOf(await admin.GetAsync($"{Base}/records/{openingId}"))).GetProperty("rowVersion").GetString()
        });
        edit.IsSuccessStatusCode.Should().BeFalse("分錄記錄不可修改");
    }

    [Fact]
    public async Task OpenV4_EventCheckAndLedger_AllTakeEffect()
    {
        var opened = await ImportAndOpen(_admin, AppTestPackagesV4.Flow(), "ledger-flow");
        opened.GetProperty("formatVersion").GetInt32().Should().Be(4);
        Db(db => db.Set<ModuleEvent>().Count()).Should().Be(1);
        Db(db => db.Set<ModuleCheck>().Count()).Should().Be(1);
        Db(db => db.Set<ModuleLedger>().Count()).Should().Be(1);
        await DriveFlow(_admin, opened);
    }

    [Fact]
    public async Task ExportThenImportIntoEmptyDatabase_BehaviorSurvives()
    {
        await ImportAndOpen(_admin, AppTestPackagesV4.Flow(), "ledger-flow");
        var exported = await ExportOk("ledger-flow");
        exported.GetProperty("formatVersion").GetInt32().Should().Be(6);
        exported.GetProperty("events").GetArrayLength().Should().Be(1);
        exported.GetProperty("checks").GetArrayLength().Should().Be(1);
        exported.GetProperty("ledgers").GetArrayLength().Should().Be(1);
        // 匯出的引用是代號：模組代號與開啟時的欄位代號（保存在對照表），不是資料庫的欄位 Id
        var effect = exported.GetProperty("events")[0].GetProperty("effects")[0];
        effect.GetProperty("target").GetString().Should().Be("move");
        effect.GetProperty("set")[0].GetProperty("from").GetProperty("field").GetString().Should().Be("doc-line.item");
        exported.GetProperty("checks")[0].GetProperty("groupBy")[0].GetString().Should().Be("move.item");

        using var fresh = new TestApp();
        var admin2 = fresh.AsUser();
        var reopened = await ImportAndOpen(admin2, exported.GetRawText(), "ledger-flow",
            new { groupMap = exported.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("code").GetString()!, g => (object)new { mode = "create" }) });
        await DriveFlow(admin2, reopened);
    }

    [Fact]
    public async Task V3Package_WithChecksOrLedgersOrEvents_IsRejected()
    {
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV4.Flow(formatVersion: 3)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().Contain("events").And.Contain("checks").And.Contain("ledgers");
    }

    [Fact]
    public async Task V3Package_WithEmptySections_IsStillAccepted()
    {
        var json = AppTestPackagesV4.Flow(formatVersion: 3, events: "[]", checks: "[]", ledgers: "[]");
        (await _admin.PostAsync($"{Apps}/packages/import", Body(json))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task HiddenSourceField_RequiresAllowHiddenSource_AtImport()
    {
        var hidden = """[ { "field": "doc-line.qty", "group": "lead", "access": "hidden" } ]""";
        var rejected = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV4.Flow(fieldAccess: hidden)));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("allowHiddenSource");

        var allowed = AppTestPackagesV4.DefaultEvents.Replace(
            "\"expr\": \"-{doc-line.qty}\" }", "\"expr\": \"-{doc-line.qty}\" }, \"allowHiddenSource\": true");
        allowed.Should().NotBe(AppTestPackagesV4.DefaultEvents);
        var json = AppTestPackagesV4.Flow(fieldAccess: hidden, events: allowed);
        await ImportAndOpen(_admin, json, "ledger-flow");
    }

    [Theory]
    [InlineData("target", "\"target\": \"move\"", "\"target\": \"ghost\"")]
    [InlineData("field", "\"from\": { \"side\": \"line\", \"field\": \"doc-line.item\" }", "\"from\": { \"side\": \"line\", \"field\": \"doc-line.ghost\" }")]
    [InlineData("state", "\"to\": \"done\" }", "\"to\": \"ghost\" }")]
    [InlineData("custom", "\"kind\": \"create\"", "\"kind\": \"custom\"")]
    public async Task EventWithBadReference_IsRejectedAtImport(string name, string find, string replace)
    {
        var events = AppTestPackagesV4.DefaultEvents.Replace(find, replace);
        events.Should().NotBe(AppTestPackagesV4.DefaultEvents, name);
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV4.Flow(events: events)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, name + await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventsThatTriggerEachOther_AreRejectedAtImport()
    {
        // 異動寫入（建立）→ 觸發另一個以異動為來源的建立事件 → 回頭寫異動：模組層級的環
        var loop = """
            [ { "code": "ledger-flow-a", "name": { "zh-TW": "甲" }, "active": true, "source": "move",
                "trigger": { "on": "create" }, "when": null,
                "effects": [ { "code": "x", "kind": "create", "target": "move", "set": [ { "to": "move.item", "const": "x" } ] } ] } ]
            """;
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(AppTestPackagesV4.Flow(events: loop)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("循環");
    }

    [Fact]
    public async Task ExcludingAFieldACheckUses_SkipsTheCheckWithAWarning()
    {
        var extra = """, { "code": "loc", "type": "text", "label": { "zh-TW": "loc" }, "config": { "isFilterable": true } }""";
        var checks = """
            [ { "code": "ledger-flow-by-loc", "name": { "zh-TW": "各位置" }, "active": true, "module": "move",
                "groupBy": ["move.loc"], "function": "sum", "field": "move.qty", "where": null,
                "assert": { "op": "gte", "limit": { "const": 0 } }, "message": { "zh-TW": "不足" } } ]
            """;
        var opened = await ImportAndOpen(_admin, AppTestPackagesV4.Flow(checks: checks, extraMoveFields: extra), "ledger-flow",
            new { groupMap = new Dictionary<string, object> { ["lead"] = new { mode = "create" } }, excludeFields = new[] { "move.loc" } });
        Db(db => db.Set<ModuleCheck>().Count()).Should().Be(0);
        opened.GetRawText().Should().Contain("ledger-flow-by-loc");   // 警告文字
    }

    [Fact]
    public async Task EventKeyAlreadyUsed_BlocksOpening()
    {
        await ImportAndOpen(_admin, AppTestPackagesV4.Flow(), "ledger-flow");
        var other = AppTestPackagesV4.Flow(appId: "ledger-two");
        (await _admin.PostAsync($"{Apps}/packages/import", Body(other))).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/ledger-two/open", CreateLead);
        res.IsSuccessStatusCode.Should().BeFalse();
        (await res.Content.ReadAsStringAsync()).Should().Contain("ledger-flow-post");
    }
}
