using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class RulesApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;
    private readonly TestApp _app;

    public RulesApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private static object Rule(string kind, string[] targets, object? condition = null, long[]? optionIds = null,
        object? message = null, bool active = true) =>
        new { kind, condition = condition ?? new { all = Array.Empty<object>() }, targets, optionIds, message, active };

    private static object Cond(long fieldId) => new { field = fieldId, op = "isEmpty" };

    private Task<HttpResponseMessage> Put(string key, params object[] rules) =>
        _admin.PutAsJsonAsync($"{Base}/modules/{key}/rules", new { rules });

    private async Task<int> VersionOf(string key) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("version").GetInt32();

    private async Task ExpectInvalid(HttpResponseMessage res, string errorKey, string message, bool startsWith = false)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var msg = (await JsonOf(res)).GetProperty("errors").GetProperty(errorKey)[0].GetString();
        if (startsWith) msg.Should().StartWith(message); else msg.Should().Be(message);
    }

    [Fact]
    public async Task PutRules_ReplacesAll_AndBumpsVersion()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        var b = await AddField(_admin, key, "integer");
        var v0 = await VersionOf(key);

        var first = await Put(key,
            Rule("visibility", [$"field:{a.Id}"], Cond(b.Id)),
            Rule("required", [$"field:{b.Id}"], Cond(a.Id)));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(first)).GetArrayLength().Should().Be(2);
        var v1 = await VersionOf(key);
        v1.Should().BeGreaterThan(v0);

        var second = await Put(key, Rule("readOnly", ["*"], Cond(a.Id)));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await VersionOf(key)).Should().BeGreaterThan(v1);

        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/rules"));
        got.GetArrayLength().Should().Be(1);
        got[0].GetProperty("kind").GetString().Should().Be("readOnly");
        got[0].GetProperty("targets")[0].GetString().Should().Be("*");
        got[0].GetProperty("condition").GetProperty("field").GetInt64().Should().Be(a.Id);
        got[0].GetProperty("active").GetBoolean().Should().BeTrue();
        got[0].GetProperty("optionIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PutRules_KeepsArrayOrder()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        await Put(key, Rule("required", [$"field:{a.Id}"]), Rule("visibility", [$"field:{a.Id}"]));
        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/rules"));
        got.EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Equal("required", "visibility");
    }

    [Fact]
    public async Task PutRules_TargetsInvalid_Rejected()
    {
        var key = await CreateModule(_admin);
        var text = await AddField(_admin, key, "text");
        var en = await AddField(_admin, key, "enum");
        var gone = await AddField(_admin, key, "text");
        (await _admin.PutAsJsonAsync($"{Base}/fields/{gone.Id}/status", new { status = "inactive" })).EnsureSuccessStatusCode();
        var other = await CreateModule(_admin);
        var foreign = await AddField(_admin, other, "text");

        var cases = new object[]
        {
            Rule("visibility", ["*"]),
            Rule("required", [$"block:{text.BlockId}"]),
            Rule("optionFilter", [$"field:{en.Id}", $"field:{text.Id}"], optionIds: []),
            Rule("optionFilter", [$"field:{text.Id}"], optionIds: []),
            Rule("required", [$"field:{gone.Id}"]),
            Rule("required", [$"field:{foreign.Id}"]),
            Rule("required", []),
            Rule("required", ["field:012"]),
            Rule("validation", [$"block:{text.BlockId}"], message: Zh("x")),
        };
        for (var i = 0; i < cases.Length; i++)
            await ExpectInvalid(await Put(key, cases[i]), "rules[0].targets", "規則目標不正確");

        // 錯誤鍵帶陣列索引
        await ExpectInvalid(await Put(key, Rule("required", [$"field:{text.Id}"]), Rule("visibility", ["*"])),
            "rules[1].targets", "規則目標不正確");
    }

    [Fact]
    public async Task PutRules_ValidationWithoutMessage_Rejected()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        await ExpectInvalid(await Put(key, Rule("validation", [$"field:{a.Id}"])), "rules[0].message", "驗證規則必須有訊息");
        await ExpectInvalid(await Put(key, Rule("validation", [$"field:{a.Id}"], message: new Dictionary<string, string> { ["en-US"] = "x" })),
            "rules[0].message", "驗證規則必須有訊息");
        (await Put(key, Rule("validation", [$"field:{a.Id}"], message: Zh("不可為空")))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PutRules_OptionNotOfTarget_Rejected()
    {
        var key = await CreateModule(_admin);
        var e1 = await AddField(_admin, key, "enum");
        var e2 = await AddField(_admin, key, "enum");
        var o1 = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{e1.Id}/options", new { label = Zh("甲") }))).GetProperty("id").GetInt64();
        var o2 = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{e2.Id}/options", new { label = Zh("乙") }))).GetProperty("id").GetInt64();

        await ExpectInvalid(await Put(key, Rule("optionFilter", [$"field:{e1.Id}"], optionIds: [o2])),
            "rules[0].optionIds", "選項不屬於目標欄位");

        var ok = await Put(key, Rule("optionFilter", [$"field:{e1.Id}"], optionIds: [o1]));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(ok))[0].GetProperty("optionIds")[0].GetInt64().Should().Be(o1);
    }

    [Fact]
    public async Task PutRules_BadCondition_Rejected()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        await ExpectInvalid(await Put(key, Rule("required", [$"field:{a.Id}"], new { field = 999999, op = "isEmpty" })),
            "rules[0].condition", "規則條件不正確：", startsWith: true);
        await ExpectInvalid(await Put(key, Rule("required", [$"field:{a.Id}"], new[] { 1 })),
            "rules[0].condition", "規則條件不正確：", startsWith: true);
    }

    [Fact]
    public async Task PutRules_Over100_Rejected()
    {
        var key = await CreateModule(_admin);
        var a = await AddField(_admin, key, "text");
        var rules = Enumerable.Range(0, 101).Select(_ => Rule("required", [$"field:{a.Id}"])).ToArray();
        await ExpectInvalid(await Put(key, rules), "rules", "規則最多 100 條");
        (await Put(key, rules.Take(100).ToArray())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PutRules_NonDesigner_403()
    {
        var key = await CreateModule(_admin);
        var res = await _app.AsUser("sales01").PutAsJsonAsync($"{Base}/modules/{key}/rules", new { rules = Array.Empty<object>() });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetFieldStatus_ReferencedByRule_Rejected()
    {
        var key = await CreateModule(_admin);
        var cond = await AddField(_admin, key, "integer");
        var target = await AddField(_admin, key, "text");
        var refd = await AddField(_admin, key, "integer");
        (await Put(key,
            Rule("required", [$"field:{target.Id}"], new { field = cond.Id, op = "gt", value = new { fieldRef = refd.Id } }))).EnsureSuccessStatusCode();

        foreach (var id in new[] { cond.Id, target.Id, refd.Id })
            await ExpectInvalid(await _admin.PutAsJsonAsync($"{Base}/fields/{id}/status", new { status = "inactive" }),
                "Status", "此欄位被公式或規則引用，不能停用");

        // 規則停用後即可停用欄位
        (await Put(key,
            Rule("required", [$"field:{target.Id}"], new { field = cond.Id, op = "isEmpty" }, active: false))).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"{Base}/fields/{cond.Id}/status", new { status = "inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
