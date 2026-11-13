using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>簽核流程端點經 HTTP：增改刪、排序、狀態機的需簽核設定與鎖定狀態（簽核設計 §15）。</summary>
public class ApprovalFlowsApiTests
{
    private const string Flows = Base + "/approval-flows";

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private static object Flow(string key, long to, int priority, long groupId, long? amountField = null, long min = 0) => new
    {
        key, name = Zh(key), toOptionId = to, priority, active = true,
        definition = new
        {
            shape = "sequence",
            when = amountField is null ? null : (object)new { all = new[] { new { field = amountField, op = "gte", value = min } } },
            stages = new[]
            {
                new
                {
                    code = "lead", name = Zh("主管"), approvers = new { source = "group", groupIds = new[] { groupId } },
                    mode = "any", editableFields = Array.Empty<long>(), dueHours = (int?)24, onEmpty = "fallback"
                }
            }
        }
    };

    [Fact]
    public async Task ApprovalFlowsApi_CrudOrderAndWorkflow()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var key = await CreateModule(c);
        var status = await AddField(c, key, "enum");
        var amount = await AddField(c, key, "decimal");
        async Task<long> Option(string label) =>
            (await JsonOf(await c.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = Zh(label), color = (string?)null })))
            .GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var done = await Option("已核准");
        await Activate(c, key);
        var group = (await JsonOf(await c.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales-manager").GetProperty("id").GetInt64();
        object Workflow(bool approval) => new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done } },
            transitions = new[]
            {
                new { from = draft, to = done, groupIds = Array.Empty<long>(), approval = approval ? new { required = true, noMatch = "direct" } : null }
            }
        };
        (await c.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(false))).EnsureSuccessStatusCode();

        var created = await c.PostAsJsonAsync($"{Flows}?module={key}", Flow("big", done, 1, group, amount.Id, 1000));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await JsonOf(created);
        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("definition").GetProperty("stages")[0].GetProperty("approvers").GetProperty("source").GetString().Should().Be("group");
        (await c.PostAsJsonAsync($"{Flows}?module={key}", Flow("small", done, 2, group))).StatusCode.Should().Be(HttpStatusCode.Created);

        var wf = await c.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(true));
        wf.StatusCode.Should().Be(HttpStatusCode.OK);
        var wfBody = await JsonOf(wf);
        wfBody.GetProperty("lockedStates").EnumerateArray().Select(x => x.GetInt64()).Should().Equal(done);
        wfBody.GetProperty("lockedFieldIds").EnumerateArray().Select(x => x.GetInt64()).Should().Contain(amount.Id);
        wfBody.GetProperty("transitions")[0].GetProperty("approval").GetProperty("noMatch").GetString().Should().Be("direct");

        (await c.PutAsJsonAsync($"{Flows}/order", new { module = key, toOptionId = done, keys = new[] { "small", "big" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await JsonOf(await c.GetAsync($"{Flows}?module={key}"));
        list.EnumerateArray().Select(f => (f.GetProperty("key").GetString(), f.GetProperty("priority").GetInt32()))
            .Should().Equal(("small", 1), ("big", 2));

        (await c.PutAsJsonAsync($"{Flows}/big", Flow("big", done, 2, group, amount.Id, 5000))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await c.GetAsync($"{Flows}/big"))).GetProperty("definition").GetProperty("when").GetRawText().Should().Contain("5000");

        (await c.DeleteAsync($"{Flows}/big")).StatusCode.Should().Be(HttpStatusCode.OK);
        var last = await c.DeleteAsync($"{Flows}/small");
        last.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(last)).GetProperty("errors").GetProperty($"states[{done}]")[0].GetString().Should().Be("狀態「已核准」需要簽核，但還沒有任何簽核流程");

        (await app.AsUser("sales01").GetAsync($"{Flows}?module={key}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    /// <summary>建一個有狀態機（草稿 → 已核准，需簽核）與一個流程 big 的模組，回傳模組鍵、欄位與狀態。</summary>
    private static async Task<(string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group)> SeedApproval(HttpClient c)
    {
        var key = await CreateModule(c);
        var status = await AddField(c, key, "enum");
        var amount = await AddField(c, key, "decimal");
        async Task<long> Option(string label) =>
            (await JsonOf(await c.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = Zh(label), color = (string?)null })))
            .GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var done = await Option("已核准");
        await Activate(c, key);
        var group = (await JsonOf(await c.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales-manager").GetProperty("id").GetInt64();
        object Workflow(bool approval) => new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done } },
            transitions = new[]
            {
                new { from = draft, to = done, groupIds = Array.Empty<long>(), approval = approval ? new { required = true, noMatch = "block" } : null }
            }
        };
        (await c.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(false))).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync($"{Flows}?module={key}", Flow("big", done, 1, group, amount.Id, 1000))).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(true))).EnsureSuccessStatusCode();
        return (key, status.Id, amount.Id, amount.Key, draft, done, group);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task GetFlows_Json_HasCurrentVersionUsed()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var list = await JsonOf(await c.GetAsync($"{Flows}?module={s.Key}"));
        list[0].GetProperty("currentVersionUsed").GetBoolean().Should().BeFalse();
    }
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task GetWorkflow_Json_HasEditableFieldIds()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var body = await JsonOf(await c.GetAsync($"{Base}/modules/{s.Key}/workflow"));
        var ids = body.GetProperty("editableFieldIds").EnumerateArray().Select(x => x.GetInt64()).ToList();
        ids.Should().Contain(s.Amount).And.NotContain(s.Status);
    }
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DraftPreview_Json_HasFlowKey()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var before = await JsonOf(await c.GetAsync($"{Flows}?module={s.Key}"));
        var draft = Flow("draft-new", s.Done, 9, s.Group);
        var res = await c.PostAsJsonAsync($"{Flows}/preview?module={s.Key}", new { draft, initiatorUserId = "sales01" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        // 優先序 1 的 big 有條件（金額 ≥ 1000）、沒給金額不成立，草稿命中
        body.GetProperty("flowKey").GetString().Should().Be("draft-new");
        // 沒有問題時 problem 為 null（JSON 省略）；有問題的情境在 ApplicationTests
        body.GetProperty("stages").GetArrayLength().Should().Be(1);
        (await JsonOf(await c.GetAsync($"{Flows}?module={s.Key}"))).GetArrayLength().Should().Be(before.GetArrayLength());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DraftPreview_NonDesigner_Returns403()
    {
        using var app = new TestApp();
        var s = await SeedApproval(app.AsUser());
        var res = await app.AsUser("sales01").PostAsJsonAsync($"{Flows}/preview?module={s.Key}",
            new { draft = Flow("draft-new", s.Done, 9, s.Group), initiatorUserId = "sales01" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DraftPreview_Json_ProblemKeyPresentOnlyWhenProblem()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        // 發起人 admin 沒有主管：主管來源找不到人、備援（系統管理員）也被發起人自己排除時才有問題；
        // 沒有問題的情境（群組有成員）不應出現 problem 鍵
        var withGroup = await JsonOf(await c.PostAsJsonAsync($"{Flows}/preview?module={s.Key}",
            new { draft = Flow("draft-new", s.Done, 9, s.Group), initiatorUserId = "sales01" }));
        withGroup.GetProperty("stages")[0].TryGetProperty("problem", out _).Should().BeFalse();

        var manager = Flow("draft-mgr", s.Done, 9, s.Group);
        var json = System.Text.Json.JsonSerializer.Serialize(new { draft = manager, initiatorUserId = "admin" });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["draft"]!["definition"]!["stages"]![0]!["approvers"] = System.Text.Json.Nodes.JsonNode.Parse("{\"source\":\"manager\",\"level\":1}");
        var res = await c.PostAsync($"{Flows}/preview?module={s.Key}", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("stages")[0].GetProperty("problem").GetString().Should().Be("noApprover");
    }

    /// <summary>送出一張金額 5000 的單據，讓流程 big（金額 ≥ 1000）的目前版本被實例綁定。</summary>
    private static async Task UseBigFlow(HttpClient c, (string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group) s)
    {
        var title = await TitleKey(c, s.Key);
        var created = await JsonOf(await c.PostAsJsonAsync($"{Base}/modules/{s.Key}/records",
            new { values = new Dictionary<string, object> { [title] = "單據", [s.AmountKey] = 5000 } }));
        var submit = await c.PostAsJsonAsync($"{Base}/records/{created.GetProperty("id").GetInt64()}/approval/submit",
            new { to = s.Done, recordRowVersion = created.GetProperty("rowVersion").GetString() });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        (await JsonOf(await c.GetAsync($"{Flows}/big"))).GetProperty("currentVersionUsed").GetBoolean().Should().BeTrue();
    }

    private static async Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> PutEdited(
        HttpClient c, System.Text.Json.JsonElement flow, Action<System.Text.Json.Nodes.JsonNode> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(flow.GetRawText())!;
        edit(node);
        var res = await c.PutAsync($"{Flows}/big", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        return (res.StatusCode, await JsonOf(res));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task UpdateFlow_UsedVersion_OnlyActiveChanged_RoundTrip_KeepsVersion()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        await UseBigFlow(c, s);
        var before = await JsonOf(await c.GetAsync($"{Flows}/big"));

        var (status, after) = await PutEdited(c, before, n => n["active"] = false);

        status.Should().Be(HttpStatusCode.OK);
        after.GetProperty("active").GetBoolean().Should().BeFalse();
        after.GetProperty("version").GetInt32().Should().Be(1);
        (await JsonOf(await c.GetAsync($"{Flows}/big"))).GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task UpdateFlow_UsedVersion_StageChanged_RoundTrip_NewVersion()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        await UseBigFlow(c, s);
        var before = await JsonOf(await c.GetAsync($"{Flows}/big"));

        var (status, after) = await PutEdited(c, before, n => n["definition"]!["stages"]![0]!["dueHours"] = 48);

        status.Should().Be(HttpStatusCode.OK);
        after.GetProperty("version").GetInt32().Should().Be(2);
        after.GetProperty("currentVersionUsed").GetBoolean().Should().BeFalse();
    }
}
