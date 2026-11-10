using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class RuleWriteTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _sales;
    private readonly TestApp _app;

    public RuleWriteTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        _sales = app.AsUser("sales01");
    }

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private static object Rule(string kind, string[] targets, object condition, long[]? optionIds = null, string? message = null) =>
        new { kind, condition, targets, optionIds, message = message is null ? null : Zh(message), active = true };

    private static object Cond(long field, string op, object? value = null) =>
        value is null ? new { field, op } : new { field, op, value };

    private async Task PutRules(string key, params object[] rules) =>
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/rules", new { rules })).EnsureSuccessStatusCode();

    private async Task Go(string key)
    {
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
    }

    private Task<HttpResponseMessage> Post(string key, Dictionary<string, object> values) =>
        _sales.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values });

    private Task<HttpResponseMessage> Put(JsonElement record, Dictionary<string, object> values) =>
        _sales.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}",
            new { values, rowVersion = record.GetProperty("rowVersion").GetString() });

    private static async Task<string> ErrorOf(HttpResponseMessage res, string errorKey)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return (await JsonOf(res)).GetProperty("errors").GetProperty(errorKey)[0].GetString()!;
    }

    private static async Task<long> AddOption(HttpClient c, long fieldId, string label) =>
        (await JsonOf(await c.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label), color = "#fff" })))
        .GetProperty("id").GetInt64();

    [Fact]
    public async Task Create_ValidationRuleFails_400OnTarget()
    {
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "integer");
        await PutRules(key, Rule("validation", [$"field:{qty.Id}"], Cond(qty.Id, "gt", 5), message: "數量要大於 5"));
        await Go(key);
        var title = await TitleKey(_admin, key);

        (await ErrorOf(await Post(key, new() { [title] = "t", [qty.Key] = 1 }), qty.Key)).Should().Be("數量要大於 5");
        (await Post(key, new() { [title] = "t", [qty.Key] = 10 })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_ValidationRuleMessage_FollowsXLocale()
    {
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "integer");
        var message = new Dictionary<string, string> { ["zh-TW"] = "數量要大於 5", ["en-US"] = "Quantity must be over 5" };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/rules", new
        {
            rules = new[] { new { kind = "validation", condition = Cond(qty.Id, "gt", 5), targets = new[] { $"field:{qty.Id}" }, message, active = true } }
        })).EnsureSuccessStatusCode();
        await Go(key);
        var title = await TitleKey(_admin, key);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/modules/{key}/records")
        {
            Content = JsonContent.Create(new { values = new Dictionary<string, object> { [title] = "t", [qty.Key] = 1 } })
        };
        req.Headers.Add("X-Locale", "en-US");
        (await ErrorOf(await _sales.SendAsync(req), qty.Key)).Should().Be("Quantity must be over 5");
        (await ErrorOf(await Post(key, new() { [title] = "t", [qty.Key] = 1 }), qty.Key)).Should().Be("數量要大於 5");
    }

    [Fact]
    public async Task Create_RuleRequired_Enforced()
    {
        var key = await CreateModule(_admin);
        var flag = await AddField(_admin, key, "boolean");
        var note = await AddField(_admin, key, "text");
        await PutRules(key, Rule("required", [$"field:{note.Id}"], Cond(flag.Id, "eq", true)));
        await Go(key);
        var title = await TitleKey(_admin, key);

        (await ErrorOf(await Post(key, new() { [title] = "t", [flag.Key] = true }), note.Key)).Should().Be("此欄位必填");
        (await Post(key, new() { [title] = "t", [flag.Key] = false })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_RuleHiddenRequired_Skipped()
    {
        var key = await CreateModule(_admin);
        var flag = await AddField(_admin, key, "boolean");
        var note = await AddField(_admin, key, "text", new { required = true });
        await PutRules(key, Rule("visibility", [$"field:{note.Id}"], Cond(flag.Id, "eq", true)));
        await Go(key);
        var title = await TitleKey(_admin, key);

        (await Post(key, new() { [title] = "t", [flag.Key] = false })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ErrorOf(await Post(key, new() { [title] = "t", [flag.Key] = true }), note.Key)).Should().Be("此欄位必填");
    }

    [Fact]
    public async Task Update_ReadOnlyAllBecomesTrue_SaveSucceedsThenLocks()
    {
        var key = await CreateModule(_admin);
        var closed = await AddField(_admin, key, "boolean");
        var memo = await AddField(_admin, key, "text");
        await PutRules(key, Rule("readOnly", ["*"], Cond(closed.Id, "eq", true)));
        await Go(key);
        var title = await TitleKey(_admin, key);
        var created = await JsonOf(await Post(key, new() { [title] = "t", [closed.Key] = false, [memo.Key] = "a" }));

        var closing = await Put(created, new() { [closed.Key] = true, [memo.Key] = "b" });
        closing.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await JsonOf(closing);
        saved.GetProperty("values").GetProperty(closed.Key).GetBoolean().Should().BeTrue();
        saved.GetProperty("values").GetProperty(memo.Key).GetString().Should().Be("b");

        (await ErrorOf(await Put(saved, new() { [memo.Key] = "c" }), "_record")).Should().Be("此記錄目前不可修改");
        var after = await JsonOf(await _sales.GetAsync($"{Base}/records/{saved.GetProperty("id").GetInt64()}"));
        after.GetProperty("values").GetProperty(memo.Key).GetString().Should().Be("b");
    }

    [Fact]
    public async Task Update_RuleReadOnlyField_InputIgnored()
    {
        var key = await CreateModule(_admin);
        var closed = await AddField(_admin, key, "boolean");
        var memo = await AddField(_admin, key, "text");
        await PutRules(key, Rule("readOnly", [$"field:{memo.Id}"], Cond(closed.Id, "eq", true)));
        await Go(key);
        var title = await TitleKey(_admin, key);
        var created = await JsonOf(await Post(key, new() { [title] = "t", [closed.Key] = false, [memo.Key] = "a" }));

        var closing = await JsonOf(await Put(created, new() { [closed.Key] = true, [memo.Key] = "b" }));
        closing.GetProperty("values").GetProperty(memo.Key).GetString().Should().Be("b"); // 這次存檔前尚未唯讀

        var res = await Put(closing, new() { [memo.Key] = "c" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("values").GetProperty(memo.Key).GetString().Should().Be("b");
    }

    [Fact]
    public async Task Update_ConfigReadOnly_InputIgnored()
    {
        var key = await CreateModule(_admin);
        var locked = await AddField(_admin, key, "text", new { readOnly = true, defaultValue = "預設" });
        await Go(key);
        var title = await TitleKey(_admin, key);

        var createdRes = await Post(key, new() { [title] = "t", [locked.Key] = "x" });
        createdRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonOf(createdRes);
        created.GetProperty("values").GetProperty(locked.Key).GetString().Should().Be("預設"); // 新增只套預設值

        var res = await Put(created, new() { [locked.Key] = "y" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("values").GetProperty(locked.Key).GetString().Should().Be("預設");
    }

    private async Task<(string Key, string Title, (long Id, string Key, long BlockId) Memo, (long Id, string Key, long BlockId) Parent,
        (long Id, string Key, long BlockId) Child, long A, long A1, long A2, long B, long B1)> OptionFilterModule()
    {
        var key = await CreateModule(_admin);
        var memo = await AddField(_admin, key, "text");
        var parent = await AddField(_admin, key, "enum");
        var child = await AddField(_admin, key, "enum");
        var a = await AddOption(_admin, parent.Id, "甲");
        var b = await AddOption(_admin, parent.Id, "乙");
        var a1 = await AddOption(_admin, child.Id, "甲一");
        var a2 = await AddOption(_admin, child.Id, "甲二");
        var b1 = await AddOption(_admin, child.Id, "乙一");
        return (key, await TitleKey(_admin, key), memo, parent, child, a, a1, a2, b, b1);
    }

    /// <summary>上層選甲時下層只能選 forA，選乙時只能選乙一。</summary>
    private static object[] ChildRules(
        (string Key, string Title, (long Id, string Key, long BlockId) Memo, (long Id, string Key, long BlockId) Parent,
            (long Id, string Key, long BlockId) Child, long A, long A1, long A2, long B, long B1) m, long[] forA) => [
        Rule("optionFilter", [$"field:{m.Child.Id}"], Cond(m.Parent.Id, "eq", $"opt_{m.A}"), forA),
        Rule("optionFilter", [$"field:{m.Child.Id}"], Cond(m.Parent.Id, "eq", $"opt_{m.B}"), [m.B1])];

    [Fact]
    public async Task Update_OptionFilterParentChanged_StaleChildRejected()
    {
        var m = await OptionFilterModule();
        await PutRules(m.Key, ChildRules(m, [m.A1, m.A2]));
        await Go(m.Key);
        var created = await JsonOf(await Post(m.Key, new()
            { [m.Title] = "t", [m.Parent.Key] = $"opt_{m.A}", [m.Child.Key] = $"opt_{m.A1}" }));

        (await ErrorOf(await Put(created, new() { [m.Parent.Key] = $"opt_{m.B}" }), m.Child.Key)).Should().Be("選項不在可選範圍");
        (await Put(created, new() { [m.Parent.Key] = $"opt_{m.B}", [m.Child.Key] = $"opt_{m.B1}" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_OptionFilterOutOfRange_Rejected()
    {
        var m = await OptionFilterModule();
        await PutRules(m.Key, ChildRules(m, [m.A1]));
        await Go(m.Key);

        (await ErrorOf(await Post(m.Key, new()
            { [m.Title] = "t", [m.Parent.Key] = $"opt_{m.A}", [m.Child.Key] = $"opt_{m.B1}" }), m.Child.Key))
            .Should().Be("選項不在可選範圍");
    }

    [Fact]
    public async Task Update_OptionFilterUntouched_OtherEditSucceeds()
    {
        var m = await OptionFilterModule();
        await PutRules(m.Key, ChildRules(m, [m.A1, m.A2]));
        await Go(m.Key);
        var created = await JsonOf(await Post(m.Key, new()
            { [m.Title] = "t", [m.Parent.Key] = $"opt_{m.A}", [m.Child.Key] = $"opt_{m.A1}" }));
        await PutRules(m.Key, ChildRules(m, [m.A2])); // 規則改了，A1 已不在可選範圍

        var res = await Put(created, new() { [m.Memo.Key] = "只改備註" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("values").GetProperty(m.Child.Key).GetString().Should().Be($"opt_{m.A1}");
    }

    [Fact]
    public async Task Create_ValidationTargetHiddenForUser_ErrorOnRecordKey()
    {
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "integer");
        await PutRules(key, Rule("validation", [$"field:{qty.Id}"], Cond(qty.Id, "gt", 5), message: "數量要大於 5"));
        await Activate(_admin, key);
        var salesId = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var priv = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = false };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            groups = new[] { new { groupId = salesId, privileges = priv, scope = "all" } },
            users = Array.Empty<object>(),
            fields = new[] { new { fieldId = qty.Id, groupId = salesId, access = "hidden" } },
            options = Array.Empty<object>()
        })).EnsureSuccessStatusCode();
        var title = await TitleKey(_admin, key);

        (await ErrorOf(await Post(key, new() { [title] = "t", [qty.Key] = 10 }), "_record")).Should().Be("數量要大於 5");
    }

    [Fact]
    public async Task Create_RuleUsesFormulaResult()
    {
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "integer");
        var total = await AddField(_admin, key, "formula", new { formula = $"{{{qty.Id}}}*1000", resultType = "number" });
        await PutRules(key, Rule("validation", [$"field:{qty.Id}"], Cond(total.Id, "lte", 100000), message: "總額不得超過 100000"));
        await Go(key);
        var title = await TitleKey(_admin, key);

        (await ErrorOf(await Post(key, new() { [title] = "t", [qty.Key] = 200 }), qty.Key)).Should().Be("總額不得超過 100000");
        (await Post(key, new() { [title] = "t", [qty.Key] = 50 })).StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
