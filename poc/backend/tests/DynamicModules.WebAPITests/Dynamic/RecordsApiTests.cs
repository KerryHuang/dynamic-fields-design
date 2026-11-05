using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class RecordsApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _sales;
    private readonly HttpClient _admin;
    private readonly TestApp _app;

    public RecordsApiTests(TestApp app)
    {
        _app = app;
        _sales = app.AsUser("sales01");
        _admin = app.AsUser(); // 設計器操作用
    }

    private async Task<(string Module, string Field, string Title)> ActiveModuleWithDecimal()
    {
        var key = await CreateModule(_admin);
        var f = await AddField(_admin, key, "decimal", new { scale = 2 });
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        return (key, f.Key, await TitleKey(_admin, key));
    }

    [Fact]
    public async Task EndToEnd_CreateModuleAddFieldActivateCreateRecord()
    {
        var (module, field, title) = await ActiveModuleWithDecimal();

        var create = await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = 12.5 } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await JsonOf(create)).GetProperty("id").GetInt64();

        var got = await JsonOf(await _sales.GetAsync($"{Base}/records/{id}"));
        got.GetProperty("values").GetProperty(field).GetDecimal().Should().Be(12.5m);
        got.GetProperty("ownerUserId").GetString().Should().Be("sales01");

        var paged = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/paged?currentPage=1&pagesSize=10"));
        paged.GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task PostRecord_InvalidValue_Returns400WithFieldKeyError()
    {
        var (module, field, title) = await ActiveModuleWithDecimal();

        var res = await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = "abc" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task PutRecord_StaleRowVersion_Returns409()
    {
        var (module, field, title) = await ActiveModuleWithDecimal();
        var created = await JsonOf(await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = 1 } }));
        var id = created.GetProperty("id").GetInt64();
        var rowVersion = created.GetProperty("rowVersion").GetString();

        var first = await _sales.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = 2 }, rowVersion });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await _sales.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = 3 }, rowVersion });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteRecord_UsesRowVersionQuery_ThenGetReturns404()
    {
        var (module, field, title) = await ActiveModuleWithDecimal();
        var created = await JsonOf(await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [field] = 1 } }));
        var id = created.GetProperty("id").GetInt64();
        var rowVersion = created.GetProperty("rowVersion").GetString();

        (await _sales.DeleteAsync($"{Base}/records/{id}?rowVersion={rowVersion}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _sales.GetAsync($"{Base}/records/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string Enc(object o) => Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(o));

    private async Task<(string Module, long AmountId, string Amount, string Cat, string Title, long CatId)> ListModule()
    {
        var key = await CreateModule(_admin);
        var amount = await AddField(_admin, key, "decimal", new { scale = 2, isFilterable = true, isSortable = true });
        var cat = await AddField(_admin, key, "text", new { isSortable = true });
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        return (key, amount.Id, amount.Key, cat.Key, await TitleKey(_admin, key), cat.Id);
    }

    private async Task<JsonElement> ExpectBadRequest(string module, string query, string errorKey)
    {
        var res = await _sales.GetAsync($"{Base}/modules/{module}/records/paged?{query}");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(res);
        body.GetProperty("errors").TryGetProperty(errorKey, out var arr).Should().BeTrue();
        return arr;
    }

    [Fact]
    public async Task Paged_FilterOnNonFilterableField_Returns400()
    {
        var (module, _, _, _, _, catId) = await ListModule();
        var err = await ExpectBadRequest(module, $"filter={Enc(new { field = catId, op = "eq", value = "a" })}", "filter");

        err[0].GetString().Should().Be($"欄位 {catId} 未開放篩選");
    }

    [Fact]
    public async Task Paged_MalformedFilterJson_Returns400()
    {
        var (module, _, _, _, _, _) = await ListModule();

        var err = await ExpectBadRequest(module, "filter=" + Uri.EscapeDataString("{not json"), "filter");

        err[0].GetString().Should().Be("篩選條件格式錯誤");
    }

    [Fact]
    public async Task Paged_SortByNonSortable_Returns400()
    {
        var (module, _, _, _, title, _) = await ListModule();

        var err = await ExpectBadRequest(module, $"sortBy={title}&sortOrder=asc", "sortBy");

        err[0].GetString().Should().Be($"欄位 {title} 不存在或未開放排序");
    }

    [Fact]
    public async Task Paged_SortByMismatchedOrderCount_Returns400()
    {
        var (module, _, amount, cat, _, _) = await ListModule();

        var err = await ExpectBadRequest(module, $"sortBy={amount}&sortBy={cat}&sortOrder=asc", "sortBy");

        err[0].GetString().Should().Be("sortBy 與 sortOrder 數量必須相同");
    }

    [Fact]
    public async Task Paged_FilterSortAndKeyword_EndToEnd()
    {
        var (module, amountId, amount, cat, title, _) = await ListModule();
        for (var i = 0; i < 12; i++)
        {
            var values = new Dictionary<string, object>
            {
                [title] = (i < 8 ? "item-" : "other-") + i,
                [amount] = i % 4,
                [cat] = i % 3 == 0 ? "a" : "b"
            };
            (await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values })).EnsureSuccessStatusCode();
        }

        var res = await JsonOf(await _sales.GetAsync(
            $"{Base}/modules/{module}/records/paged?filter={Enc(new { field = amountId, op = "gte", value = 2 })}" +
            $"&sortBy={amount}&sortOrder=desc&sortBy={cat}&sortOrder=asc&q=item"));

        res.GetProperty("totalCount").GetInt32().Should().Be(4);
        res.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString())
            .Should().Equal("item-3", "item-7", "item-6", "item-2");
    }

    [Fact]
    public async Task Paged_NoParams_KeepsPhase1Behavior()
    {
        var (module, _, amount, _, title, _) = await ListModule();
        foreach (var n in new[] { "a", "b", "c" })
            (await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
                new { values = new Dictionary<string, object> { [title] = n, [amount] = 1 } })).EnsureSuccessStatusCode();

        var res = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/paged"));

        res.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetInt64())
            .Should().BeInDescendingOrder().And.HaveCount(3);
    }

    private async Task<(string Module, string Title)> LightModule(params string[] titles)
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);
        foreach (var t in titles)
            (await _sales.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object> { [title] = t } }))
                .EnsureSuccessStatusCode();
        return (key, title);
    }

    private void WithDb(Action<DynamicDbContext> act)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Light_KeywordAndPaging()
    {
        var (module, _) = await LightModule("蘋果", "香蕉", "櫻桃");

        var hit = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/light?keyword=香"));
        hit.GetProperty("total").GetInt32().Should().Be(1);
        hit.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("香蕉");

        var p1 = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/light?size=2&page=1"));
        p1.GetProperty("total").GetInt32().Should().Be(3);
        p1.GetProperty("items").GetArrayLength().Should().Be(2);
        var p2 = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/light?size=2&page=2"));
        p2.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Light_OutOfScope_Excluded()
    {
        var (module, title) = await LightModule("我的");
        (await _app.AsUser("sales02").PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "別人的" } })).EnsureSuccessStatusCode();
        WithDb(db =>
        {
            var m = db.Set<Module>().Single(x => x.Key == module);
            var sales = db.Set<PermissionGroup>().Single(g => g.Key == "sales");
            db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = sales.Id, Scope = DataScope.Own });
        });

        var res = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/light"));

        res.GetProperty("total").GetInt32().Should().Be(1);
        res.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("我的");
    }

    [Fact]
    public async Task Light_TitleHidden_Forbidden()
    {
        var (module, _) = await LightModule("a");
        WithDb(db =>
        {
            var m = db.Set<Module>().Single(x => x.Key == module);
            var sales = db.Set<PermissionGroup>().Single(g => g.Key == "sales");
            db.Set<FieldPermission>().Add(new FieldPermission { FieldId = m.TitleFieldId!.Value, GroupId = sales.Id, Access = FieldAccess.Hidden });
        });

        (await _sales.GetAsync($"{Base}/modules/{module}/records/light")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Light_InactiveModule_NotFound()
    {
        var key = await CreateModule(_admin);

        var res = await _sales.GetAsync($"{Base}/modules/{key}/records/light");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("模組不存在或未啟用");
    }
    // ---- 表頭與明細一起存檔 ----

    private async Task<(string Parent, string Child, string ParentTitle, string ChildTitle, string Qty)> DetailModules()
    {
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        await AddField(_admin, child, "reference", new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        var qty = await AddField(_admin, child, "decimal", new { scale = 2 });
        await Activate(_admin, parent);
        await Activate(_admin, child);
        _app.GrantBusinessGroups(parent);
        return (parent, child, await TitleKey(_admin, parent), await TitleKey(_admin, child), qty.Key);
    }

    [Fact]
    public async Task PostRecord_DetailsJson_Shape()
    {
        var (parent, child, parentTitle, childTitle, qty) = await DetailModules();
        var create = await _sales.PostAsJsonAsync($"{Base}/modules/{parent}/records", new
        {
            values = new Dictionary<string, object> { [parentTitle] = "t" },
            details = new Dictionary<string, object[]>
            {
                [child] = [new { values = new Dictionary<string, object> { [childTitle] = "a", [qty] = 1 }, _delete = false }]
            }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var header = await JsonOf(create);
        var row = header.GetProperty("details").GetProperty(child)[0];
        row.GetProperty("values").GetProperty(qty).GetDecimal().Should().Be(1m);

        var put = await _sales.PutAsJsonAsync($"{Base}/records/{header.GetProperty("id").GetInt64()}", new
        {
            values = new { },
            rowVersion = header.GetProperty("rowVersion").GetString(),
            details = new Dictionary<string, object[]>
            {
                [child] = [new { id = row.GetProperty("id").GetInt64(), rowVersion = row.GetProperty("rowVersion").GetString(), _delete = true }]
            }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(put)).GetProperty("details").GetProperty(child).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ErrorKeys_DetailFormat()
    {
        var (parent, child, parentTitle, childTitle, qty) = await DetailModules();

        var res = await _sales.PostAsJsonAsync($"{Base}/modules/{parent}/records", new
        {
            values = new Dictionary<string, object> { [parentTitle] = "t" },
            details = new Dictionary<string, object[]>
            {
                [child] = [new { values = new Dictionary<string, object> { [childTitle] = "a", [qty] = "abc" } }],
                ["foo"] = []
            }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.TryGetProperty($"details.{child}[0].{qty}", out _).Should().BeTrue(errors.ToString());
        errors.GetProperty("details.foo")[0].GetString().Should().Be("明細模組不存在或未啟用");
    }
}
