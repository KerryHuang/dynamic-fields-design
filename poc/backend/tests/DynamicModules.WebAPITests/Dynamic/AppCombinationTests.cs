using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>三個內建應用程式（CRM 與銷售、MES 模具廠、進銷存）彼此獨立：只開其中一個、兩個、三個都能用；關掉一個，其他的與既有資料不受影響。</summary>
public class AppCombinationTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppCombinationTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private Dictionary<string, long> GroupIds()
    {
        using var scope = _app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().ToDictionary(g => g.Key, g => g.Id);
    }

    private object MapOf(params (string Code, string Group)[] map)
    {
        var ids = GroupIds();
        return new { groupMap = map.ToDictionary(m => m.Code, m => (object)new { mode = "map", groupId = ids[m.Group] }) };
    }

    private async Task<JsonElement> Open(string id)
    {
        var body = id switch
        {
            "mes-mold" => MapOf(("sales", "sales"), ("salesManager", "sales-manager"), ("floor", "floor"), ("maint", "maint"), ("qa", "qa"), ("planner", "planner")),
            "crm-sales" => MapOf(("sales", "sales"), ("manager", "sales-manager")),
            _ => MapOf(("buyer", "purchasing"), ("keeper", "warehouse"), ("manager", "ops-manager"))
        };
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/{id}/open", body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string KeyOf(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private async Task<string> FieldKey(string moduleKey, string label)
    {
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == label).GetProperty("key").GetString()!;
    }

    private async Task<HttpResponseMessage> CreateItem(JsonElement inventory, string name) =>
        await _admin.PostAsJsonAsync($"{Base}/modules/{KeyOf(inventory, "item")}/records",
            new { values = new Dictionary<string, object?> { [await FieldKey(KeyOf(inventory, "item"), "名稱")] = name } });

    private async Task<HttpResponseMessage> CreateWarehouse(JsonElement inventory, string code, string name)
    {
        var key = KeyOf(inventory, "warehouse");
        return await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object?> { [await FieldKey(key, "倉庫代號")] = code, [await FieldKey(key, "名稱")] = name } });
    }

    private async Task<int> Count(string moduleKey) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{moduleKey}/records/paged?currentPage=1&pagesSize=50"))).GetProperty("totalCount").GetInt32();

    private async Task<HashSet<string>> MenuKeys() =>
        (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToHashSet();

    [Fact]
    public async Task MesOnly_OpensAndCreatesNothingOfTheOthers()
    {
        var mes = await Open("mes-mold");

        var menu = await MenuKeys();
        menu.Should().Contain(KeyOf(mes, "workorder")).And.OnlyContain(k => !k.StartsWith("inv-"));
        (await _admin.GetAsync($"{Base}/modules/inv-item")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InventoryOnly_OpensAndWorksWithoutMes()
    {
        var inv = await Open("inventory");

        (await CreateItem(inv, "示範品項")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await MenuKeys()).Should().OnlyContain(k => !k.StartsWith("mes-"));
    }

    [Fact]
    public async Task AllThree_OpenTogether_AndEachKeepsItsOwnModulesReportsAndCodes()
    {
        var crm = await Open("crm-sales");
        var mes = await Open("mes-mold");
        var inv = await Open("inventory");

        var keys = new[] { crm, mes, inv }.SelectMany(a => a.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()!)).ToList();
        keys.Should().OnlyHaveUniqueItems("模組鍵不重複");
        var apps = await JsonOf(await _admin.GetAsync(Apps));
        apps.EnumerateArray().Select(a => a.GetProperty("key").GetString()).Should()
            .BeEquivalentTo(["crm-sales", "mes-mold", "inventory", "partner", "contact"], "CRM 用到的共用主檔隨它自動開啟");

        // 事件、檢查、列印範本、轉換的代碼全站不重複（各套件用 mes-／inv-／crm- 前綴）
        var catalog = _app.Services.GetRequiredService<DynamicModules.Application.Dynamic.AppService.AppCatalog>();
        var codes = catalog.BuiltIns.SelectMany(p => (p.Events ?? []).Select(e => e.Code)
            .Concat((p.Checks ?? []).Select(c => c.Code)).Concat((p.PrintTemplates ?? []).Select(t => t.Code))
            .Concat((p.Conversions ?? []).Select(c => c.Code))).ToList();
        codes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ClosingOne_LeavesTheOthersWritable_AndItsOwnDataReadable_AndReopenKeepsIt()
    {
        var mes = await Open("mes-mold");
        var inv = await Open("inventory");
        (await CreateItem(inv, "示範品項")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await CreateWarehouse(inv, "WH-1", "主倉")).StatusCode.Should().Be(HttpStatusCode.Created);

        (await _admin.PostAsJsonAsync($"{Apps}/mes-mold/close", new { cascade = false })).EnsureSuccessStatusCode();

        // 進銷存不受影響，MES 的模組唯讀
        (await CreateItem(inv, "關閉後新增的品項")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Count(KeyOf(inv, "item"))).Should().Be(2);
        var woKey = KeyOf(mes, "workorder");
        (await _admin.GetAsync($"{Base}/modules/{woKey}/records/paged?currentPage=1&pagesSize=10")).StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await _admin.PostAsJsonAsync($"{Base}/modules/{woKey}/records", new { values = new Dictionary<string, object?>() });
        ((int)blocked.StatusCode).Should().BeInRange(400, 499);

        (await _admin.PostAsync($"{Apps}/mes-mold/reopen", null)).EnsureSuccessStatusCode();
        (await Count(KeyOf(inv, "item"))).Should().Be(2);
    }

    [Fact]
    public async Task ClosingInventory_LeavesMesWritable()
    {
        var mes = await Open("mes-mold");
        var inv = await Open("inventory");
        (await _admin.PostAsJsonAsync($"{Apps}/inventory/close", new { cascade = false })).EnsureSuccessStatusCode();

        var key = KeyOf(mes, "quote");
        var quote = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new
        {
            values = new Dictionary<string, object?> { [await FieldKey(key, "客戶")] = await PartnerAsync(_admin, "宏達科技"), [await FieldKey(key, "報價日期")] = "2026-11-01" }
        });
        quote.StatusCode.Should().Be(HttpStatusCode.Created, await quote.Content.ReadAsStringAsync());
        ((int)(await CreateItem(inv, "不該成功")).StatusCode).Should().BeInRange(400, 499);
    }
}
