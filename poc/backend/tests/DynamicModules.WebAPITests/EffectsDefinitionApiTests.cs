using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>事件、檢查、分錄的設計器端點：路由、權限（只有設計器 Edit 才能寫）、錯誤回應的形狀。</summary>
public class EffectsDefinitionApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public EffectsDefinitionApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private static object Name(string key) => new Dictionary<string, string> { ["zh-TW"] = key };

    [Fact]
    public async Task Designer_endpoints_are_forbidden_for_business_users()
    {
        var sales = _app.AsUser("sales01");
        foreach (var path in new[] { "events", "checks", "modules/x/ledger" })
            (await sales.GetAsync($"{Base}/{path}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        var ev = new
        {
            key = "ev-x", name = Name("x"), sourceModuleKey = "x", active = true,
            definition = new { trigger = new { on = "create" }, effects = Array.Empty<object>() }
        };
        (await sales.PostAsJsonAsync($"{Base}/events", ev)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PutAsJsonAsync($"{Base}/modules/x/ledger", new { reversalFieldId = 1, negate = Array.Empty<long>(), swap = Array.Empty<long[]>(), confirmFreeze = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Check_roundtrips_over_http_with_camelCase_json()
    {
        var m = await CreateModule(_admin);
        var item = await AddField(_admin, m, "text");
        var qty = await AddField(_admin, m, "decimal");
        await Activate(_admin, m);
        var key = "chk-" + Guid.NewGuid().ToString("N")[..8];

        var create = await _admin.PostAsJsonAsync($"{Base}/checks", new
        {
            key, name = Name(key), moduleKey = m, active = true,
            definition = new
            {
                groupBy = new[] { item.Id }, function = "sum", fieldId = qty.Id,
                assert = new { op = "gte", limit = new { @const = 0 } }, message = Name("不足")
            }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());

        var got = await JsonOf(await _admin.GetAsync($"{Base}/checks/{key}"));
        got.GetProperty("definition").GetProperty("assert").GetProperty("limit").GetProperty("const").GetDecimal().Should().Be(0);

        var bad = await _admin.PostAsJsonAsync($"{Base}/checks", new
        {
            key = key + "x", name = Name(key), moduleKey = m, active = true,
            definition = new { groupBy = new[] { qty.Id }, function = "sum", fieldId = qty.Id, assert = new { op = "gte", limit = new { @const = 0 } }, message = Name("x") }
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty("definition.groupBy").GetArrayLength().Should().BeGreaterThan(0);

        (await _admin.DeleteAsync($"{Base}/checks/{key}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Base}/checks/{key}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ledger_endpoint_saves_and_conflicts_when_removing_with_records()
    {
        var m = await CreateModule(_admin);
        var rev = await AddField(_admin, m, "reference", new { refTarget = new { kind = "module", key = m }, readOnly = true });
        var qty = await AddField(_admin, m, "decimal");
        await Activate(_admin, m);
        _app.GrantBusinessGroups(m);

        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{m}/ledger",
            new { reversalFieldId = rev.Id, negate = new[] { qty.Id }, swap = Array.Empty<long[]>(), confirmFreeze = false });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var title = await TitleKey(_admin, m);
        (await _admin.PostAsJsonAsync($"{Base}/modules/{m}/records", new { values = new Dictionary<string, object> { [title] = "a" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.DeleteAsync($"{Base}/modules/{m}/ledger")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
