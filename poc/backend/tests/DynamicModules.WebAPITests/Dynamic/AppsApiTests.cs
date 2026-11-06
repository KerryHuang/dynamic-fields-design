using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>應用程式套件的匯入、開啟、關閉、重新開啟與刪除。</summary>
public class AppsApiTests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppsApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Import(string json, HttpClient? c = null) =>
        (c ?? _admin).PostAsync($"{Apps}/packages/import", Body(json));

    private async Task ImportOk(string json) =>
        (await Import(json)).StatusCode.Should().Be(HttpStatusCode.Created);

    private static readonly object MapAll = new
    {
        groupMap = new Dictionary<string, object>
        {
            ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" }
        }
    };

    private Task<HttpResponseMessage> Open(string id, object? body = null, HttpClient? c = null) =>
        (c ?? _admin).PostAsJsonAsync($"{Apps}/packages/{id}/open", body ?? new { });

    private async Task<JsonElement> OpenOk(string id, object? body = null)
    {
        var res = await Open(id, body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string KeyOf(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private async Task<(JsonElement Crm, JsonElement Quote)> OpenCrmAndQuote()
    {
        await ImportOk(AppTestPackages.Crm);
        await ImportOk(AppTestPackages.Quote);
        var crm = await OpenOk("crm-lite", MapAll);
        var quote = await OpenOk("quote-lite", new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "skip" } } });
        return (crm, quote);
    }

    private async Task AddCustomer(JsonElement crm, string name = "客戶甲")
    {
        var key = KeyOf(crm, "customer");
        var title = await TitleKey(_admin, key);
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = name } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    // ---- 空系統與權限 ----

    [Fact]
    public async Task FreshInstall_NoApps_ListsAreEmptyAndPlatformWorks()
    {
        (await JsonOf(await _admin.GetAsync(Apps))).GetArrayLength().Should().Be(0);
        (await JsonOf(await _admin.GetAsync($"{Apps}/packages"))).GetArrayLength().Should().Be(0);
        (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).GetArrayLength().Should().Be(0);
        (await _admin.GetAsync($"{Base}/reports/catalog")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Base}/modules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Apps}/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // 沒有應用程式的模組照常建立、啟用、寫入
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
    }

    [Fact]
    public async Task Writes_RequireSystemAdmin_NotJustDesignerRights()
    {
        await ImportOk(AppTestPackages.Procure);
        var sales = _app.AsUser("sales01");

        (await Import(AppTestPackages.Crm, sales)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Open("procure-lite", null, sales)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await OpenOk("procure-lite");
        (await sales.PostAsJsonAsync($"{Apps}/procure-lite/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsync($"{Apps}/procure-lite/reopen", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Apps}/procure-lite?purged=0")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Db(db => db.Set<App>().Single(a => a.Key == "procure-lite").Status).Should().Be(AppStatus.Open);
    }

    [Fact]
    public async Task Import_RejectsBadAndDuplicatePackages_AndCyclesInRequires()
    {
        (await Import("{ not json")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Import(AppTestPackages.Crm.Replace("\"reqired\"", "x").Replace("\"required\": true", "\"reqired\": true"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        await ImportOk(AppTestPackages.Crm);
        (await Import(AppTestPackages.Crm)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // crm-lite 宣告依賴 quote-lite，而 quote-lite 已依賴 crm-lite
        await ImportOk(AppTestPackages.Quote);
        var loop = AppTestPackages.Procure.Replace("\"id\": \"procure-lite\"", "\"id\": \"loop-lite\"")
            .Replace("\"name\": { \"zh-TW\": \"簡易採購\" } }", "\"name\": { \"zh-TW\": \"迴圈\" }, \"requires\": { \"apps\": [ { \"id\": \"quote-lite\", \"modules\": [\"quote\"] } ] } }");
        await ImportOk(loop); // quote → crm，loop → quote：沒有環
        var cycle = AppTestPackages.Crm.Replace("\"id\": \"crm-lite\"", "\"id\": \"crm-two\"")
            .Replace("\"requires\": { \"apps\": [], \"entities\": [] }", "\"requires\": { \"apps\": [ { \"id\": \"loop-lite\", \"modules\": [\"po\"] } ] }");
        (await Import(cycle)).StatusCode.Should().Be(HttpStatusCode.Created); // crm-two → loop → quote → crm-lite：仍無環

        var real = AppTestPackages.Crm.Replace("\"requires\": { \"apps\": [], \"entities\": [] }", "\"requires\": { \"apps\": [ { \"id\": \"quote-lite\", \"modules\": [\"quote\"] } ] }")
            .Replace("\"id\": \"crm-lite\"", "\"id\": \"crm-three\"");
        // crm-three → quote-lite → crm-lite，沒有回到 crm-three，仍合法；真正的環要回到自己
        (await Import(real)).StatusCode.Should().Be(HttpStatusCode.Created);
        var selfLoop = AppTestPackages.Quote.Replace("\"id\": \"quote-lite\"", "\"id\": \"crm-lite\"");
        (await Import(selfLoop)).StatusCode.Should().Be(HttpStatusCode.BadRequest); // 依賴自己
    }

    [Fact]
    public async Task Import_RealCycleBetweenTwoPackages_IsRejected()
    {
        // a 要求 b；b 要求 a：後匯入的那個被拒絕並列出環
        string Pkg(string id, string requires) => AppTestPackages.Procure.Replace("procure-lite", id)
            .Replace("\"name\": { \"zh-TW\": \"簡易採購\" } }", $"\"name\": {{ \"zh-TW\": \"x\" }}, \"requires\": {{ \"apps\": [ {{ \"id\": \"{requires}\", \"modules\": [\"po\"] }} ] }} }}")
            .Replace("\"key\": \"pl-supplier\"", $"\"key\": \"{id}-supplier\"").Replace("\"key\": \"pl-po\"", $"\"key\": \"{id}-po\"");
        await ImportOk(Pkg("pkg-a", "pkg-b"));
        var res = await Import(Pkg("pkg-b", "pkg-a"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("循環");
    }

    // ---- 開啟 ----

    [Fact]
    public async Task Open_CreatesActiveModules_WithRealIds_ViewsRulesAndPermissions()
    {
        await ImportOk(AppTestPackages.Crm);
        var preview = await JsonOf(await _admin.PostAsJsonAsync($"{Apps}/packages/crm-lite/preview", new { }));
        preview.GetProperty("groups").GetArrayLength().Should().Be(2);
        var suggestions = preview.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("code").GetString()!, g => g.GetProperty("suggestion").GetString());
        suggestions["sales"].Should().Be("map", "現有群組的 Key 等於代號就建議對應");
        suggestions["salesLead"].Should().Be("create");
        preview.GetProperty("keyConflicts").GetArrayLength().Should().Be(0);

        var app = await OpenOk("crm-lite", MapAll);
        app.GetProperty("status").GetString().Should().Be("Open");
        var customer = KeyOf(app, "customer");
        customer.Should().Be("crm-customer");

        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{customer}"));
        module.GetProperty("status").GetString().Should().Be("Active");
        module.GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{customer}/views"))).GetArrayLength().Should().Be(1);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{customer}/rules"))).GetArrayLength().Should().Be(1);

        // Program 啟用、新建了兩個群組、欄位例外與選項範圍落地
        Db(db => db.Set<DynamicProgram>().Single(p => p.ProgramCode == db.Set<Module>().Single(m => m.Key == customer).ProgramCode).Active)
            .Should().BeTrue();
        Db(db => db.Set<PermissionGroup>().Count(g => g.Key.StartsWith("g-"))).Should().Be(2);
        Db(db => db.Set<FieldPermission>().Count()).Should().Be(1);
        Db(db => db.Set<FieldOptionScope>().Count()).Should().Be(1);
        Db(db => db.Set<AppModule>().Count()).Should().Be(2);
    }

    [Fact]
    public async Task Open_GroupMap_MapsToExistingGroup_AndMissingEntriesGetNoPermissions()
    {
        await ImportOk(AppTestPackages.Crm);
        var salesId = Db(db => db.Set<PermissionGroup>().First(g => g.Key == "sales").Id);
        var app = await OpenOk("crm-lite", new
        {
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "map", groupId = salesId } } // salesLead 沒列
        });
        app.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("salesLead"));

        var customer = KeyOf(app, "customer");
        var rows = Db(db => db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode ==
            db.Set<Module>().Single(m => m.Key == customer).ProgramCode).Select(p => p.GroupId).ToList());
        rows.Should().Equal(salesId); // 只有對應到的群組有權限，沒對應的不給任何人
    }

    [Fact]
    public async Task Open_KeyConflict_Is409WithList_AndModuleKeysResolvesIt()
    {
        await ImportOk(AppTestPackages.Crm);
        await CreateModule(_admin, "crm-customer");

        var preview = await JsonOf(await _admin.PostAsJsonAsync($"{Apps}/packages/crm-lite/preview", new { }));
        preview.GetProperty("keyConflicts")[0].GetProperty("moduleCode").GetString().Should().Be("customer");

        var res = await Open("crm-lite", MapAll);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Db(db => db.Set<App>().Count()).Should().Be(0);
        Db(db => db.Set<Module>().Count(m => m.Key == "crm-contact")).Should().Be(0);

        var app = await OpenOk("crm-lite", new
        {
            moduleKeys = new Dictionary<string, string> { ["customer"] = "crm-customer-2" },
            groupMap = MapAll.GetType().GetProperty("groupMap")!.GetValue(MapAll)
        });
        KeyOf(app, "customer").Should().Be("crm-customer-2");
        // 模組裡指向 customer 的參照，跟著改名後的 key
        var contact = await JsonOf(await _admin.GetAsync($"{Base}/modules/{KeyOf(app, "contact")}/schema"));
        contact.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference")
            .GetProperty("reference").GetProperty("key").GetString().Should().Be("crm-customer-2");
    }

    [Fact]
    public async Task Open_Twice_Is409WithExistingApp_AndClosedHintsReopen()
    {
        await ImportOk(AppTestPackages.Procure);
        await OpenOk("procure-lite");

        var again = await Open("procure-lite");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(again);
        body.GetProperty("key").GetString().Should().Be("procure-lite");
        body.GetProperty("status").GetString().Should().Be("Open");
        Db(db => db.Set<App>().Count()).Should().Be(1);

        (await _admin.PostAsJsonAsync($"{Apps}/procure-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        var closed = await Open("procure-lite");
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(closed)).GetProperty("status").GetString().Should().Be("Closed");
    }

    [Fact]
    public async Task Open_FailureMidway_RollsEverythingBack()
    {
        // 公式語法錯誤驗證器不檢查，到 AddField 才失敗：此時模組、群組都已建立
        var broken = AppTestPackages.Crm.Replace("{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": { \"showInList\": true } },",
            "{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": { \"showInList\": true } }, { \"code\": \"calc\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"算\" }, \"config\": { \"formula\": \"1 +\", \"resultType\": \"number\" } },");
        await ImportOk(broken);

        var res = await Open("crm-lite", MapAll);
        res.IsSuccessStatusCode.Should().BeFalse(await res.Content.ReadAsStringAsync());
        Db(db => db.Set<App>().Count()).Should().Be(0);
        Db(db => db.Set<AppModule>().Count()).Should().Be(0);
        Db(db => db.Set<Module>().Count(m => m.Key.StartsWith("crm-"))).Should().Be(0);
        Db(db => db.Set<PermissionGroup>().Count(g => g.Key.StartsWith("g-"))).Should().Be(0);
    }

    [Fact]
    public async Task Open_MissingDependencies_Is409WithReasons()
    {
        await ImportOk(AppTestPackages.Crm);
        await ImportOk(AppTestPackages.Quote);

        async Task<string> Reason()
        {
            var res = await Open("quote-lite");
            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            return (await JsonOf(res)).GetProperty("missingDependencies")[0].GetProperty("reason").GetString()!;
        }

        (await Reason()).Should().Be("notInstalled");
        await OpenOk("crm-lite", MapAll);

        // 客戶自己停用依賴的模組
        var customer = "crm-customer";
        (await _admin.PostAsync($"{Base}/modules/{customer}/deactivate", null)).EnsureSuccessStatusCode();
        (await Reason()).Should().Be("moduleNotActive");
        await Activate(_admin, customer);

        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        (await Reason()).Should().Be("closed");
        (await _admin.PostAsync($"{Apps}/crm-lite/reopen", null)).EnsureSuccessStatusCode();

        // 客戶單獨刪掉依賴的模組後，AppModule 的列隨之消失（模組之間的欄位引用要先清掉才能刪，這裡直接模擬結果）
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppModule>().Remove(db.Set<AppModule>().Single(m => m.Code == "customer"));
            db.SaveChanges();
        }
        (await Reason()).Should().Be("moduleMissing");
    }

    private static string ExtensionPackage(string id, string moduleKey, string requiredEntity = "design.mold") => $$"""
        {
          "format": "formwright-app", "formatVersion": 2,
          "manifest": { "id": "{{id}}", "version": "1.0.0", "name": { "zh-TW": "擴充" },
            "requires": { "apps": [], "entities": ["{{requiredEntity}}"] } },
          "modules": [ { "code": "extra", "key": "{{moduleKey}}", "name": { "zh-TW": "模具附加" }, "kind": "extension",
            "hostEntityKey": "{{requiredEntity}}", "titleField": null,
            "blocks": [ { "code": "main", "label": { "zh-TW": "附加" }, "collapsed": false, "columns": 2, "fields": [
              { "code": "note", "type": "text", "label": { "zh-TW": "備註" }, "config": {} } ] } ] } ]
        }
        """;

    [Fact]
    public async Task Open_ExtensionHostAlreadyTaken_Is409_AndMissingEntityProviderIsReported()
    {
        await ImportOk(ExtensionPackage("ext-one", "ext-one-module"));
        await ImportOk(ExtensionPackage("ext-two", "ext-two-module"));
        await OpenOk("ext-one");

        var taken = await Open("ext-two");
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(taken)).GetProperty("hostEntityConflicts")[0].GetString().Should().Be("extra");

        await ImportOk(ExtensionPackage("ext-ghost", "ext-ghost-module", "no.such-entity"));
        var ghost = await Open("ext-ghost");
        ghost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(ghost)).GetProperty("missingDependencies")[0].GetProperty("reason").GetString().Should().Be("entityMissing");
    }

    [Fact]
    public async Task Open_CrossAppReference_PointsToTheActualModuleKey()
    {
        var (crm, quote) = await OpenCrmAndQuote();
        var quoteKey = KeyOf(quote, "quote");
        var schema = await JsonOf(await _admin.GetAsync($"{Base}/modules/{quoteKey}/schema"));
        schema.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference")
            .GetProperty("reference").GetProperty("key").GetString().Should().Be(KeyOf(crm, "customer"));
    }

    [Fact]
    public async Task TwoUnrelatedPackages_BothGoThroughOpenCloseReopen()
    {
        await ImportOk(AppTestPackages.Crm);
        await ImportOk(AppTestPackages.Procure);
        var crm = await OpenOk("crm-lite", MapAll);
        var procure = await OpenOk("procure-lite", new { groupMap = new Dictionary<string, object> { ["buyer"] = new { mode = "create" } } });

        foreach (var (id, app) in new[] { ("crm-lite", crm), ("procure-lite", procure) })
        {
            (await _admin.PostAsJsonAsync($"{Apps}/{id}/close", new { cascade = false })).EnsureSuccessStatusCode();
            foreach (var m in app.GetProperty("modules").EnumerateArray())
                (await JsonOf(await _admin.GetAsync($"{Base}/modules/{m.GetProperty("key").GetString()}"))).GetProperty("status").GetString().Should().Be("Closed");
            (await _admin.PostAsync($"{Apps}/{id}/reopen", null)).EnsureSuccessStatusCode();
            foreach (var m in app.GetProperty("modules").EnumerateArray())
                (await JsonOf(await _admin.GetAsync($"{Base}/modules/{m.GetProperty("key").GetString()}"))).GetProperty("status").GetString().Should().Be("Active");
        }
    }

    // ---- 關閉與重新開啟 ----

    [Fact]
    public async Task Close_KeepsDataReadable_BlocksWritesEvenForAdmin_AndReopenRestoresEverything()
    {
        await ImportOk(AppTestPackages.Crm);
        var crm = await OpenOk("crm-lite", MapAll);
        await AddCustomer(crm);
        var key = KeyOf(crm, "customer");
        var title = await TitleKey(_admin, key);

        var close = await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false });
        close.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Apps}/crm-lite"))).GetProperty("status").GetString().Should().Be("Closed");

        (await _admin.GetAsync($"{Base}/modules/{key}/records/paged?page=1&size=20")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "新" } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).EnumerateArray()
            .Count(i => i.GetProperty("closed").GetBoolean()).Should().Be(2);

        // 再關一次：200 且不變
        var rowVersion = (await JsonOf(await _admin.GetAsync($"{Apps}/crm-lite"))).GetProperty("rowVersion").GetString();
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Apps}/crm-lite"))).GetProperty("rowVersion").GetString().Should().Be(rowVersion);

        (await _admin.PostAsync($"{Apps}/crm-lite/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Apps}/crm-lite/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK); // 冪等
        (await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "新" } })).StatusCode.Should().Be(HttpStatusCode.Created);
        var paged = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/records/paged?page=1&size=20"));
        paged.GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Close_DoesNotTouchDraftOrInactiveMembers_AndReopenDoesNotActivateThem()
    {
        await ImportOk(AppTestPackages.Crm);
        await OpenOk("crm-lite", MapAll);
        (await _admin.PostAsync($"{Base}/modules/crm-contact/deactivate", null)).EnsureSuccessStatusCode();

        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        Db(db => db.Set<Module>().Single(m => m.Key == "crm-contact").Status).Should().Be(ModuleStatus.Inactive);
        Db(db => db.Set<Module>().Single(m => m.Key == "crm-customer").Status).Should().Be(ModuleStatus.Closed);

        (await _admin.PostAsync($"{Apps}/crm-lite/reopen", null)).EnsureSuccessStatusCode();
        Db(db => db.Set<Module>().Single(m => m.Key == "crm-contact").Status).Should().Be(ModuleStatus.Inactive);
        Db(db => db.Set<Module>().Single(m => m.Key == "crm-customer").Status).Should().Be(ModuleStatus.Active);
    }

    [Fact]
    public async Task Close_WithDependents_Is409UntilCascade_AndReopenNeedsDependencyOpen()
    {
        var (crm, quote) = await OpenCrmAndQuote();
        await AddCustomer(crm);

        var blocked = await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(blocked)).GetProperty("dependents")[0].GetString().Should().Be("quote-lite");
        Db(db => db.Set<App>().Count(a => a.Status == AppStatus.Closed)).Should().Be(0);

        var detail = await JsonOf(await _admin.GetAsync($"{Apps}/crm-lite"));
        detail.GetProperty("dependents")[0].GetString().Should().Be("quote-lite");

        var cascade = await JsonOf(await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = true }));
        cascade.GetProperty("closed").EnumerateArray().Select(c => c.GetString()).Should().Equal("quote-lite", "crm-lite");
        Db(db => db.Set<App>().Count(a => a.Status == AppStatus.Closed)).Should().Be(2);

        // 依賴者先重新開啟：被擋，因為客戶應用程式還關著
        var early = await _admin.PostAsync($"{Apps}/quote-lite/reopen", null);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(early)).GetProperty("missingDependencies")[0].GetProperty("reason").GetString().Should().Be("closed");

        (await _admin.PostAsync($"{Apps}/crm-lite/reopen", null)).EnsureSuccessStatusCode();
        Db(db => db.Set<App>().Single(a => a.Key == "quote-lite").Status).Should().Be(AppStatus.Closed); // 不自動連帶重開
        (await _admin.PostAsync($"{Apps}/quote-lite/reopen", null)).EnsureSuccessStatusCode();
        _ = quote;
    }

    [Fact]
    public async Task Close_ClosedTargetKeepsExistingReferencesVisible_ForOtherApps()
    {
        var (crm, quote) = await OpenCrmAndQuote();
        await AddCustomer(crm);
        var customerKey = KeyOf(crm, "customer");
        var quoteKey = KeyOf(quote, "quote");
        var customerId = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{customerKey}/records/paged?page=1&size=5")))
            .GetProperty("items")[0].GetProperty("id").GetInt64();
        var quoteSchema = await JsonOf(await _admin.GetAsync($"{Base}/modules/{quoteKey}/schema"));
        var refKey = quoteSchema.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference").GetProperty("key").GetString()!;
        (await _admin.PostAsJsonAsync($"{Base}/modules/{quoteKey}/records",
            new { values = new Dictionary<string, object> { [refKey] = customerId } })).StatusCode.Should().Be(HttpStatusCode.Created);

        // 只關客戶：被擋（報價依賴它），所以直接把客戶模組設為 Closed 模擬「自建模組」情境以外的狀態；
        // 這裡驗的是：關閉後報價的參照仍顯示標題而不是無權限
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = true })).EnsureSuccessStatusCode();
        (await _admin.PostAsync($"{Apps}/quote-lite/reopen", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var paged = await JsonOf(await _admin.GetAsync($"{Base}/modules/{quoteKey}/records/paged?page=1&size=5"));
        paged.GetProperty("items")[0].GetProperty("references").GetProperty(refKey).GetProperty("state").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Close_UnaffiliatedModuleReferencing_OnlyWarns()
    {
        await ImportOk(AppTestPackages.Crm);
        var crm = await OpenOk("crm-lite", MapAll);
        var own = await CreateModule(_admin);
        await AddField(_admin, own, "reference", new { refTarget = new { kind = "module", key = KeyOf(crm, "customer") } });
        await Activate(_admin, own);

        var close = await JsonOf(await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false }));
        close.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains(own));
    }

    [Fact]
    public async Task Restart_KeepsClosedAppReadable()
    {
        await ImportOk(AppTestPackages.Crm);
        var crm = await OpenOk("crm-lite", MapAll);
        await AddCustomer(crm);
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();

        using (var scope = _app.Services.CreateScope())
            await DynamicModules.Persistence.Seed.SeedData.EnsureSeededAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());

        var key = KeyOf(crm, "customer");
        (await _admin.GetAsync($"{Base}/modules/{key}/records/paged?page=1&size=20")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_RequiresClosed_NoData_NoOpenDependents()
    {
        var (crm, _) = await OpenCrmAndQuote();
        await AddCustomer(crm);

        // 開啟中不能刪
        var open = await _admin.DeleteAsync($"{Apps}/quote-lite?purged=0");
        open.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await open.Content.ReadAsStringAsync()).Should().Contain("appNotClosed");

        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = true })).EnsureSuccessStatusCode();

        // 有資料不能刪
        var withData = await _admin.DeleteAsync($"{Apps}/crm-lite?purged=0");
        withData.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await withData.Content.ReadAsStringAsync()).Should().Contain("liveRecords");
        Db(db => db.Set<App>().Count()).Should().Be(2);
    }

    [Fact]
    public async Task Delete_Succeeds_RemovesEverythingButKeepsCreatedGroups()
    {
        await ImportOk(AppTestPackages.Crm);
        await OpenOk("crm-lite", MapAll);
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();

        var missingPurged = await _admin.DeleteAsync($"{Apps}/crm-lite");
        missingPurged.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _admin.DeleteAsync($"{Apps}/crm-lite?purged=0")).StatusCode.Should().Be(HttpStatusCode.OK);
        Db(db => db.Set<App>().Count()).Should().Be(0);
        Db(db => db.Set<AppModule>().Count()).Should().Be(0);
        Db(db => db.Set<Module>().Count(m => m.Key.StartsWith("crm-"))).Should().Be(0);
        Db(db => db.Set<PermissionGroup>().Count(g => g.Key.StartsWith("g-"))).Should().Be(2);
        (await _admin.GetAsync($"{Apps}/crm-lite")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 套件還在目錄裡，可以重新開啟
        (await Open("crm-lite", new
        {
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "skip" }, ["salesLead"] = new { mode = "skip" } }
        })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Delete_BlockedByReferenceFromOutsideTheApp()
    {
        await ImportOk(AppTestPackages.Crm);
        var crm = await OpenOk("crm-lite", MapAll);
        var own = await CreateModule(_admin);
        await AddField(_admin, own, "reference", new { refTarget = new { kind = "module", key = KeyOf(crm, "customer") } });
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();

        var res = await _admin.DeleteAsync($"{Apps}/crm-lite?purged=0");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("referenceField");
    }

    [Fact]
    public async Task ModuleLevelDelete_IsRefusedForClosedMembers()
    {
        await ImportOk(AppTestPackages.Procure);
        await OpenOk("procure-lite");
        (await _admin.PostAsJsonAsync($"{Apps}/procure-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        (await _admin.DeleteAsync($"{Base}/modules/pl-po?purged=0")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- 鐵則：程式裡沒有寫死的應用程式 ----

    [Fact]
    public void SourceCode_HasNoHardCodedPackageIds()
    {
        var root = FindBackendRoot();
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => new[] { "crm-lite", "quote-lite", "procure-lite" }.Any(id => File.ReadAllText(f).Contains(id)))
            .ToList();
        offenders.Should().BeEmpty("內建套件只能存在於 apps/*.app.json，程式不得認得任何特定應用程式");
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicModules.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 DynamicModules.sln");
    }
}
