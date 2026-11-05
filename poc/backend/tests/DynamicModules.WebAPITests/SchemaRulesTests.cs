using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class SchemaRulesTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _sales;
    private readonly TestApp _app;

    public SchemaRulesTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        _sales = app.AsUser("sales01");
    }

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private static object Rule(string kind, string[] targets, object condition, long[]? optionIds = null, bool active = true) =>
        new { kind, condition, targets, optionIds, message = Zh("訊息"), active };

    private static object Cond(long field) => new { field, op = "isEmpty" };

    private async Task PutRules(string key, params object[] rules) =>
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/rules", new { rules })).EnsureSuccessStatusCode();

    private async Task Go(string key, params long[] hiddenFromSales)
    {
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        if (hiddenFromSales.Length == 0) return;
        var salesId = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var priv = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = false };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            groups = new[] { new { groupId = salesId, privileges = priv, scope = "all" } },
            users = Array.Empty<object>(),
            fields = hiddenFromSales.Select(id => new { fieldId = id, groupId = salesId, access = "hidden" }).ToArray(),
            options = Array.Empty<object>()
        })).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> Rules(HttpClient c, string key) =>
        (await JsonOf(await c.GetAsync($"{Base}/modules/{key}/schema"))).GetProperty("rules");

    [Fact]
    public async Task Schema_IncludesActiveRules()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        var b = await AddField(_admin, key, "text");
        await PutRules(key,
            Rule("visibility", [$"field:{a.Id}"], Cond(b.Id)),
            Rule("required", [$"field:{b.Id}"], Cond(a.Id), active: false),
            Rule("validation", [$"field:{b.Id}"], Cond(a.Id)));
        await Go(key);

        var rules = await Rules(_sales, key);
        rules.EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Equal("visibility", "validation");
        rules[0].GetProperty("condition").GetProperty("field").GetInt64().Should().Be(b.Id);
        rules[0].GetProperty("targets")[0].GetString().Should().Be($"field:{a.Id}");
        rules[1].GetProperty("message").GetProperty("zh-TW").GetString().Should().Be("訊息");
    }

    [Fact]
    public async Task Schema_RuleReferencingHiddenField_Omitted()
    {
        var key = await CreateModule(_admin);
        var note = await AddField(_admin, key, "text");
        var amount = await AddField(_admin, key, "integer");
        await PutRules(key, Rule("visibility", [$"field:{note.Id}"], Cond(amount.Id)));
        await Go(key, amount.Id);

        (await Rules(_sales, key)).GetArrayLength().Should().Be(0);
        (await Rules(_admin, key)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Schema_RuleTargetHidden_TargetRemovedOrRuleDropped()
    {
        var key = await CreateModule(_admin);
        var note = await AddField(_admin, key, "text");
        var amount = await AddField(_admin, key, "integer");
        var cond = Cond(note.Id);
        var block2 = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{key}/blocks", new { label = Zh("區塊二") })))
            .GetProperty("id").GetInt64();
        var secret = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{key}/fields",
            new { blockId = block2, type = "text", label = Zh("機密"), config = new { } }))).GetProperty("id").GetInt64();
        await PutRules(key,
            Rule("required", [$"field:{amount.Id}", $"field:{note.Id}"], cond),
            Rule("required", [$"field:{amount.Id}"], cond),
            Rule("readOnly", ["*", $"field:{amount.Id}"], cond),
            Rule("visibility", [$"block:{block2}", $"field:{note.Id}"], cond),
            Rule("visibility", [$"block:{block2}"], cond),
            Rule("validation", [$"field:{secret}"], cond));
        await Go(key, amount.Id, secret);

        var rules = (await Rules(_sales, key)).EnumerateArray().ToList();
        rules.Select(r => string.Join(",", r.GetProperty("targets").EnumerateArray().Select(t => t.GetString()))).Should().Equal(
            $"field:{note.Id}", "*", $"field:{note.Id}");
        (await Rules(_admin, key)).GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task Schema_OptionFilterOptions_AsOptValues()
    {
        var key = await CreateModule(_admin);
        var note = await AddField(_admin, key, "text");
        var en = await AddField(_admin, key, "enum");
        var o1 = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{en.Id}/options", new { label = Zh("甲"), color = "#fff" })))
            .GetProperty("id").GetInt64();
        await PutRules(key, Rule("optionFilter", [$"field:{en.Id}"], Cond(note.Id), [o1]));
        await Go(key);

        var rule = (await Rules(_sales, key))[0];
        rule.GetProperty("options").EnumerateArray().Select(o => o.GetString()).Should().Equal($"opt_{o1}");
    }
}
