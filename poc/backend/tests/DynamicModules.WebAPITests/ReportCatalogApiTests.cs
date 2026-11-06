using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class ReportCatalogApiTests
{
    private const string Catalog = Base + "/reports/catalog";

    private static long GroupId(TestApp app, string key)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().Single(g => g.Key == key).Id;
    }

    /// <summary>整組換掉模組授權；每個項目是群組鍵，皆給完整動作。</summary>
    private static async Task Grant(TestApp app, string module, object[]? fields = null, params string[] groups) => await Grant(app, module, fields, pricing: true, groups);

    private static async Task Grant(TestApp app, string module, object[]? fields, bool pricing, params string[] groups)
    {
        var res = await app.AsUser().PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
        {
            groups = groups.Select(g => new
            {
                groupId = GroupId(app, g),
                privileges = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing, approve = false },
                scope = "all"
            }).ToArray(),
            users = Array.Empty<object>(), fields = fields ?? [], options = Array.Empty<object>()
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static IEnumerable<long> Ids(JsonElement fields) => fields.EnumerateArray().Select(f => f.GetProperty("id").GetInt64());

    private static async Task<string> NewActive(TestApp app, Func<string, Task>? setup = null)
    {
        var key = await CreateModule(app.AsUser());
        if (setup is not null) await setup(key);
        await Activate(app.AsUser(), key);
        app.GrantBusinessGroups(key);
        return key;
    }

    [Fact]
    public async Task Catalog_ExcludesHiddenAndRollupAndIndirectFormula()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var line = await CreateModule(admin);
        var main = await CreateModule(admin);
        var parentRef = await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = main }, relation = "detail" });
        var qty = await AddField(admin, line, "decimal", new { scale = 0 });
        var amount = await AddField(admin, main, "decimal", new { scale = 0 });
        var secret = await AddField(admin, main, "decimal", new { scale = 0 });
        var rollup = await AddField(admin, main, "rollup", new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = qty.Id } });
        var direct = await AddField(admin, main, "formula", new { formula = $"{{{rollup.Id}}} + 1", resultType = "number", scale = 0 });
        var indirect = await AddField(admin, main, "formula", new { formula = $"{{{direct.Id}}} * 2", resultType = "number", scale = 0 });
        var plain = await AddField(admin, main, "formula", new { formula = $"{{{amount.Id}}} * 2", resultType = "number", scale = 0 });
        await Activate(admin, main);
        await Activate(admin, line);
        app.GrantBusinessGroups(main);
        await Grant(app, main, [new { fieldId = secret.Id, groupId = GroupId(app, "sales"), access = "hidden" }], "mis", "sales");

        var sales = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));
        var asAdmin = await JsonOf(await admin.GetAsync($"{Catalog}/{main}"));

        var salesIds = Ids(sales.GetProperty("fields")).ToList();
        salesIds.Should().Contain([amount.Id, plain.Id]);
        salesIds.Should().NotContain([secret.Id, rollup.Id, direct.Id, indirect.Id]);
        Ids(asAdmin.GetProperty("fields")).Should().Contain(secret.Id).And.NotContain([rollup.Id, direct.Id, indirect.Id]);
        var amountField = sales.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == amount.Id);
        amountField.GetProperty("canSum").GetBoolean().Should().BeTrue();
        amountField.GetProperty("canMinMax").GetBoolean().Should().BeTrue();
        amountField.GetProperty("isDate").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_ListsDetailAndBackReferenceChildren()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var line = await CreateModule(admin);
        var back = await CreateModule(admin);
        var lineRef = await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = main }, relation = "detail" });
        var lineQty = await AddField(admin, line, "decimal", new { scale = 0 });
        var backRef = await AddField(admin, back, "reference", new { refTarget = new { kind = "module", key = main } });
        var backQty = await AddField(admin, back, "decimal", new { scale = 0 });
        foreach (var m in new[] { main, line, back }) await Activate(admin, m);
        foreach (var m in new[] { main, line, back }) app.GrantBusinessGroups(m);

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        var children = json.GetProperty("children").EnumerateArray().ToList();
        children.Select(c => c.GetProperty("moduleKey").GetString()).Should().BeEquivalentTo([line, back]);
        var detail = children.Single(c => c.GetProperty("moduleKey").GetString() == line);
        detail.GetProperty("isDetail").GetBoolean().Should().BeTrue();
        detail.GetProperty("linkFieldId").GetInt64().Should().Be(lineRef.Id);
        Ids(detail.GetProperty("fields")).Should().Contain(lineQty.Id);
        var reverse = children.Single(c => c.GetProperty("moduleKey").GetString() == back);
        reverse.GetProperty("isDetail").GetBoolean().Should().BeFalse();
        reverse.GetProperty("linkFieldId").GetInt64().Should().Be(backRef.Id);
        Ids(reverse.GetProperty("fields")).Should().Contain(backQty.Id);
    }

    [Fact]
    public async Task Catalog_ModuleWithoutRead_NotListed()
    {
        using var app = new TestApp();
        var open = await NewActive(app);
        var closed = await NewActive(app);
        await Grant(app, closed, null, "mis", "sales");
        var floor = app.AsUser("floor01");

        var list = await JsonOf(await floor.GetAsync(Catalog));
        var salesList = await JsonOf(await app.AsUser("sales01").GetAsync(Catalog));

        list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(open).And.NotContain(closed);
        salesList.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain([open, closed]);
        (await floor.GetAsync($"{Catalog}/{closed}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Catalog_DetailModule_NotAMainOption()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var line = await CreateModule(admin);
        await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = main }, relation = "detail" });
        await Activate(admin, main);
        await Activate(admin, line);
        app.GrantBusinessGroups(main);
        app.GrantBusinessGroups(line);

        var list = await JsonOf(await admin.GetAsync(Catalog));

        list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(main).And.NotContain(line);
        (await admin.GetAsync($"{Catalog}/{line}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{Catalog}/no-such-module")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Catalog_ViaTargetNoRead_NotListed()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var target = await CreateModule(admin);
        var targetAmount = await AddField(admin, target, "decimal", new { scale = 0 });
        // 目標模組自己指回主模組的參照：帶出時不可再是參照，不列
        var main = await CreateModule(admin);
        var targetBack = await AddField(admin, target, "reference", new { refTarget = new { kind = "module", key = main } });
        var link = await AddField(admin, main, "reference", new { refTarget = new { kind = "module", key = target } });
        await Activate(admin, target);
        await Activate(admin, main);
        app.GrantBusinessGroups(target);
        app.GrantBusinessGroups(main);

        var readable = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));
        await Grant(app, target, null, "mis");
        var blocked = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        var via = readable.GetProperty("via").EnumerateArray().Single();
        via.GetProperty("fieldId").GetInt64().Should().Be(link.Id);
        via.GetProperty("targetKey").GetString().Should().Be(target);
        Ids(via.GetProperty("fields")).Should().Contain(targetAmount.Id).And.NotContain(targetBack.Id);
        Ids(readable.GetProperty("fields")).Should().Contain(link.Id);
        blocked.GetProperty("via").GetArrayLength().Should().Be(0);
        Ids(blocked.GetProperty("fields")).Should().NotContain(link.Id);
    }

    [Fact]
    public async Task Catalog_ViaTarget_ExcludesRollupAndFormulaUsingIt()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var line = await CreateModule(admin);
        var target = await CreateModule(admin);
        var parentRef = await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = target }, relation = "detail" });
        var qty = await AddField(admin, line, "decimal", new { scale = 0 });
        var plain = await AddField(admin, target, "decimal", new { scale = 0 });
        var rollup = await AddField(admin, target, "rollup", new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = qty.Id } });
        var formula = await AddField(admin, target, "formula", new { formula = $"{{{rollup.Id}}} + 1", resultType = "number", scale = 0 });
        await Activate(admin, target);
        await Activate(admin, line);
        app.GrantBusinessGroups(target);
        var main = await CreateModule(admin);
        var link = await AddField(admin, main, "reference", new { refTarget = new { kind = "module", key = target } });
        await Activate(admin, main);
        app.GrantBusinessGroups(main);

        var json = await JsonOf(await admin.GetAsync($"{Catalog}/{main}"));

        var via = json.GetProperty("via").EnumerateArray().Single(v => v.GetProperty("fieldId").GetInt64() == link.Id);
        Ids(via.GetProperty("fields")).Should().Contain(plain.Id).And.NotContain([rollup.Id, formula.Id]);
    }

    [Fact]
    public async Task Catalog_EntityTarget_TitleOnly()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var customer = await AddField(admin, main, "reference", new { refTarget = new { kind = "entity", key = "sales.customer" } });
        await Activate(admin, main);
        app.GrantBusinessGroups(main);

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        var via = json.GetProperty("via").EnumerateArray().Single();
        via.GetProperty("fieldId").GetInt64().Should().Be(customer.Id);
        var fields = via.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().ContainSingle();
        fields[0].GetProperty("id").GetInt64().Should().Be(0);
        fields[0].GetProperty("canGroup").GetBoolean().Should().BeTrue();
        fields[0].GetProperty("canSum").GetBoolean().Should().BeFalse();
        // 條件比對的是鍵，不是標題文字：目錄要讓設計器用記錄挑選器，運算子也跟參照欄位相同
        fields[0].GetProperty("type").GetString().Should().Be("reference");
        fields[0].GetProperty("refKind").GetString().Should().Be("entity");
        fields[0].GetProperty("refKey").GetString().Should().Be("sales.customer");
    }

    [Fact]
    public async Task Catalog_ReferenceField_ExposesTargetForThePicker()
    {
        using var app = new TestApp();
        var target = await NewActive(app);
        var main = await CreateModule(app.AsUser());
        var toModule = await AddField(app.AsUser(), main, "reference", new { refTarget = new { kind = "module", key = target } });
        var toEntity = await AddField(app.AsUser(), main, "reference", new { refTarget = new { kind = "entity", key = "sales.customer" } });
        var plain = await AddField(app.AsUser(), main, "decimal", new { scale = 0 });
        await Activate(app.AsUser(), main);
        app.GrantBusinessGroups(main);

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        JsonElement Field(long id) => json.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == id);
        Field(toModule.Id).GetProperty("refKind").GetString().Should().Be("module");
        Field(toModule.Id).GetProperty("refKey").GetString().Should().Be(target);
        Field(toEntity.Id).GetProperty("refKind").GetString().Should().Be("entity");
        Field(toEntity.Id).GetProperty("refKey").GetString().Should().Be("sales.customer");
        // 伺服器省略 null 屬性：非參照欄位沒有 refKey
        Field(plain.Id).TryGetProperty("refKey", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_DirectReferenceToUnreadableTarget_NotListed()
    {
        using var app = new TestApp();
        var target = await NewActive(app);
        var main = await CreateModule(app.AsUser());
        var link = await AddField(app.AsUser(), main, "reference", new { refTarget = new { kind = "module", key = target } });
        var plain = await AddField(app.AsUser(), main, "decimal", new { scale = 0 });
        await Activate(app.AsUser(), main);
        app.GrantBusinessGroups(main);
        await Grant(app, target, null, "mis");

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        Ids(json.GetProperty("fields")).Should().Contain(plain.Id).And.NotContain(link.Id);
    }

    [Fact]
    public async Task Catalog_ChildWithoutRead_OrHiddenField_NotListed()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var noRead = await CreateModule(admin);
        var shown = await CreateModule(admin);
        await AddField(admin, noRead, "reference", new { refTarget = new { kind = "module", key = main } });
        var shownLink = await AddField(admin, shown, "reference", new { refTarget = new { kind = "module", key = main } });
        var shownQty = await AddField(admin, shown, "decimal", new { scale = 0 });
        var hiddenQty = await AddField(admin, shown, "decimal", new { scale = 0 });
        foreach (var m in new[] { main, noRead, shown }) { await Activate(admin, m); app.GrantBusinessGroups(m); }
        await Grant(app, noRead, null, "mis");
        await Grant(app, shown, [new { fieldId = hiddenQty.Id, groupId = GroupId(app, "sales"), access = "hidden" }], "mis", "sales");

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        var child = json.GetProperty("children").EnumerateArray().Single();
        child.GetProperty("moduleKey").GetString().Should().Be(shown);
        child.GetProperty("linkFieldId").GetInt64().Should().Be(shownLink.Id);
        Ids(child.GetProperty("fields")).Should().Contain(shownQty.Id).And.NotContain(hiddenQty.Id);
    }

    [Fact]
    public async Task Catalog_EnumOptions_KeepInactiveWithFlag_HiddenFieldHasNone()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var status = await AddField(admin, main, "enum", new { });
        var secret = await AddField(admin, main, "enum", new { });
        var live = await AddOpt(admin, status.Id, "新單");
        var old = await AddOpt(admin, status.Id, "舊單");
        await AddOpt(admin, secret.Id, "機密");
        (await admin.PutAsJsonAsync($"{Base}/options/{old}", new { label = new Dictionary<string, string> { ["zh-TW"] = "舊單" }, active = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await Activate(admin, main);
        app.GrantBusinessGroups(main);
        await Grant(app, main, [new { fieldId = secret.Id, groupId = GroupId(app, "sales"), access = "hidden" }], "mis", "sales");

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        var options = json.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == status.Id)
            .GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!, o => o.GetProperty("active").GetBoolean());
        options.Should().Contain(new KeyValuePair<string, bool>($"opt_{live}", true)).And.Contain(new KeyValuePair<string, bool>($"opt_{old}", false));
        Ids(json.GetProperty("fields")).Should().NotContain(secret.Id);
        json.ToString().Should().NotContain("機密");
    }

    private static async Task<long> AddOpt(HttpClient c, long fieldId, string label)
    {
        var res = await c.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label } });
        res.EnsureSuccessStatusCode();
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_CurrencyField_FollowsPricing(bool pricing)
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var price = await AddField(admin, main, "currency", new { });
        await Activate(admin, main);
        app.GrantBusinessGroups(main);
        await Grant(app, main, null, pricing, "mis", "sales");

        var json = await JsonOf(await app.AsUser("sales01").GetAsync($"{Catalog}/{main}"));

        if (pricing) Ids(json.GetProperty("fields")).Should().Contain(price.Id);
        else Ids(json.GetProperty("fields")).Should().NotContain(price.Id);
    }
}
