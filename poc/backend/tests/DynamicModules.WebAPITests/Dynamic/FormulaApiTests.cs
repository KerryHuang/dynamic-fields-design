using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class FormulaApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _sales;
    private readonly HttpClient _admin;
    private readonly TestApp _app;

    public FormulaApiTests(TestApp app)
    {
        _app = app;
        _sales = app.AsUser("sales01");
        _admin = app.AsUser();
    }

    [Fact]
    public async Task AddFormula_BadExpression_Returns400WithConfigFormulaError()
    {
        var key = await CreateModule(_admin);
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));

        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/fields", new
        {
            blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64(),
            type = "formula",
            label = new Dictionary<string, string> { ["zh-TW"] = "合計" },
            config = new { formula = "1 +", resultType = "number" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("Config.Formula")[0].GetString()
            .Should().StartWith("公式不正確：");
    }

    [Fact]
    public async Task Formula_ComputedOnSave_InputIgnored()
    {
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "integer");
        var total = await AddField(_admin, key, "formula", new { formula = $"{{{qty.Id}}}*3", resultType = "number" });
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);

        var create = await _sales.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [qty.Key] = 4, [total.Key] = 1 } });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        (await JsonOf(create)).GetProperty("values").GetProperty(total.Key).GetDecimal().Should().Be(12m);
    }
}
