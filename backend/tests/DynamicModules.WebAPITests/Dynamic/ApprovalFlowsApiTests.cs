using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

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

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Get_UnpublishedFlow_VersionNull()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var moduleId = db.Set<Module>().Single(m => m.Key == s.Key).Id;
            db.Add(new ApprovalFlow
            {
                Key = "draft-only", NameI18n = new([KeyValuePair.Create("zh-TW", "只有草稿")]), ModuleId = moduleId, ToOptionId = s.Done, Priority = 9,
                Active = false, DraftJson = """{"rev":"seed-rev","definition":{"shape":"graph"},"layout":null}""", CreatedUserId = "admin", CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0)
            });
            db.SaveForTests();
        }

        var one = await JsonOf(await c.GetAsync($"{Flows}/draft-only"));
        // API 的 JSON 省略 null：version、definition 要不是沒有這個鍵、就是 null
        foreach (var name in new[] { "version", "definition" })
            (!one.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null).Should().BeTrue(name);
        one.GetProperty("draft").GetProperty("definition").GetProperty("shape").GetString().Should().Be("graph");
        one.GetProperty("draftRev").GetString().Should().Be("seed-rev");
        (await JsonOf(await c.GetAsync($"{Flows}?module={s.Key}"))).EnumerateArray().Select(f => f.GetProperty("key").GetString())
            .Should().Contain("draft-only");
        // 沒有版本的流程啟用是 400（先發布，§10.8）
        (await c.PutAsJsonAsync($"{Flows}/draft-only", Flow("draft-only", s.Done, 9, s.Group))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static object GraphBody(string key, (string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group) s, string mode = "bad",
        bool badEdgeLabel = false)
    {
        object Node(string id, string kind, object? more = null) => more ?? new { id, kind };
        return new
        {
            key, name = Zh("流程圖"), toOptionId = s.Done, priority = 5, active = true,
            definition = new
            {
                shape = "graph", stages = Array.Empty<object>(),
                nodes = new[]
                {
                    Node("start", "start"),
                    Node("a", "approval", new
                    {
                        id = "a", kind = "approval", name = Zh("主管"), approvers = new { source = "group", groupIds = new[] { s.Group } },
                        mode, editableFields = Array.Empty<long>(), onEmpty = "fallback"
                    }),
                    Node("end", "end")
                },
                edges = new object[]
                {
                    // 連線標籤沒有繁體中文：錯誤鍵 edges.e1.label
                    new { id = "e1", from = "start", to = "a", label = badEdgeLabel ? new Dictionary<string, string> { ["en-US"] = "x" } : null },
                    new { id = "e2", from = "a", to = "end" }
                }
            }
        };
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Create_GraphInvalid_400_NodeKeys()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var res = await c.PostAsJsonAsync($"{Flows}?module={s.Key}", GraphBody("graph-one", s, badEdgeLabel: true));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.TryGetProperty("definition.shape", out _).Should().BeFalse("fg2g2 起 API 收流程圖，不再有格式擋門");
        errors.TryGetProperty("definition.nodes.a.mode", out _).Should().BeTrue("屬性錯誤用節點代號定位");
        errors.TryGetProperty("definition.edges.e1.label", out _).Should().BeTrue("連線錯誤用連線代號定位");
        (await c.GetAsync($"{Flows}/graph-one")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Create_Graph_201_AndSubmitApprove_EndToEnd()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        app.GrantBusinessGroups(s.Key);
        // 主管 sales01 → 法務 sales02、財務 floor01 全部到齊 → 結束；API 直接存成目前版本
        var created = await c.PostAsJsonAsync($"{Flows}?module={s.Key}", AnyJoinBody("graph-one", s, "all"));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await JsonOf(created);
        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("definition").GetProperty("shape").GetString().Should().Be("graph");

        var id = await SubmitGraphRecord(c, s);
        var current = await CurrentOf(c, id);
        var mgr = TaskOf(current, "sales01");
        (await ApproveAs(app, "sales01", mgr, current)).StatusCode.Should().Be(HttpStatusCode.OK);
        current = await CurrentOf(c, id);
        (await ApproveAs(app, "sales02", TaskOf(current, "sales02"), current)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CurrentOf(c, id)).GetProperty("status").GetString().Should().Be("pending", "全部到齊：還差財務");
        (await ApproveAs(app, "floor01", TaskOf(current, "floor01"), current)).StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await JsonOf(await c.GetAsync($"{Base}/records/{id}"));
        record.GetProperty("approval").GetProperty("status").GetString().Should().Be("approved");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task RecordApproval_GraphInstance_StagesHaveCloseReason()
    {
        // 任一到齊：法務先核准，財務那一列被取消，回應帶 closeReason 與 nodeId、branchKey（U6）
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        app.GrantBusinessGroups(s.Key);
        (await c.PostAsJsonAsync($"{Flows}?module={s.Key}", AnyJoinBody("graph-any", s, "any"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await SubmitGraphRecord(c, s);
        var current = await CurrentOf(c, id);
        (await ApproveAs(app, "sales01", TaskOf(current, "sales01"), current)).StatusCode.Should().Be(HttpStatusCode.OK);
        current = await CurrentOf(c, id);
        (await ApproveAs(app, "sales02", TaskOf(current, "sales02"), current)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await JsonOf(await c.GetAsync($"{Base}/records/{id}/approval"));
        var stages = (after.TryGetProperty("current", out var cur) ? cur : after.GetProperty("previous").EnumerateArray().Single()).GetProperty("stages")
            .EnumerateArray().ToList();
        var cancelled = stages.Single(x => x.GetProperty("nodeId").GetString() == "fin");
        cancelled.GetProperty("closeReason").GetString().Should().Be("branchCancelled");
        cancelled.GetProperty("branchKey").GetString().Should().Be("ff");
        stages.Single(x => x.GetProperty("nodeId").GetString() == "legal").TryGetProperty("closeReason", out _).Should().BeFalse("沒有原因時省略");
    }

    /// <summary>主管 sales01 → 分支（法務 sales02、財務 floor01）→ 匯合（joinMode）→ 結束。</summary>
    private static object AnyJoinBody(string key, (string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group) s, string joinMode)
    {
        object Approver(string id, string user) => new
        {
            id, kind = "approval", name = Zh(id), approvers = new { source = "users", userIds = new[] { user } },
            mode = "any", editableFields = Array.Empty<long>(), onEmpty = "fallback"
        };
        return new
        {
            key, name = Zh("會簽"), toOptionId = s.Done, priority = 5, active = true,
            definition = new
            {
                shape = "graph", stages = Array.Empty<object>(),
                nodes = new object[]
                {
                    new { id = "start", kind = "start" }, Approver("mgr", "sales01"), new { id = "f", kind = "fork", name = Zh("法務與財務") },
                    Approver("legal", "sales02"), Approver("fin", "floor01"), new { id = "j", kind = "join", forkId = "f", mode = joinMode }, new { id = "end", kind = "end" }
                },
                edges = new object[]
                {
                    new { id = "e0", from = "start", to = "mgr" }, new { id = "e1", from = "mgr", to = "f" }, new { id = "fl", from = "f", to = "legal" },
                    new { id = "ff", from = "f", to = "fin" }, new { id = "lj", from = "legal", to = "j" }, new { id = "fj", from = "fin", to = "j" },
                    new { id = "je", from = "j", to = "end" }
                }
            }
        };
    }

    private static async Task<long> SubmitGraphRecord(HttpClient c, (string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group) s)
    {
        var created = await JsonOf(await c.PostAsJsonAsync($"{Base}/modules/{s.Key}/records",
            new { values = new Dictionary<string, object> { [await TitleKey(c, s.Key)] = "單據" } }));
        var id = created.GetProperty("id").GetInt64();
        var submit = await c.PostAsJsonAsync($"{Base}/records/{id}/approval/submit", new { to = s.Done, recordRowVersion = created.GetProperty("rowVersion").GetString() });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        return id;
    }

    private static async Task<System.Text.Json.JsonElement> CurrentOf(HttpClient c, long recordId) =>
        (await JsonOf(await c.GetAsync($"{Base}/records/{recordId}/approval"))).GetProperty("current");

    private static long TaskOf(System.Text.Json.JsonElement current, string user) =>
        current.GetProperty("stages").EnumerateArray().SelectMany(x => x.GetProperty("tasks").EnumerateArray())
            .Single(t => t.GetProperty("assigneeUserId").GetString() == user && t.GetProperty("status").GetString() == "open").GetProperty("id").GetInt64();

    private static Task<HttpResponseMessage> ApproveAs(TestApp app, string user, long taskId, System.Text.Json.JsonElement current) =>
        app.AsUser(user).PostAsJsonAsync($"{Base}/approval-tasks/{taskId}/approve", new { rowVersion = current.GetProperty("rowVersion").GetString() });

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Create_GraphWithNotifyNode_400()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var body = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GraphBody("graph-notify", s, mode: "any")))!;
        var nodes = body["definition"]!["nodes"]!.AsArray();
        nodes.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"tell","kind":"notify","name":{"zh-TW":"知會"},"recipients":[{"kind":"initiator"}],"template":{"title":{"zh-TW":"t"},"body":{"zh-TW":"b"}}}"""));
        var edges = body["definition"]!["edges"]!.AsArray();
        edges.Last()!["to"] = "tell";
        edges.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"e3","from":"tell","to":"end"}"""));

        var res = await c.PostAsync($"{Flows}?module={s.Key}", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("errors").GetProperty("definition.nodes.tell")[0].GetString().Should().Be("通知節點尚未開放");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Update_SequenceToGraph_200_ThenGraphToSequence_400()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var toGraph = await c.PutAsJsonAsync($"{Flows}/big", GraphBody("big", s, mode: "all") );
        toGraph.StatusCode.Should().Be(HttpStatusCode.OK, await toGraph.Content.ReadAsStringAsync());
        (await JsonOf(toGraph)).GetProperty("definition").GetProperty("shape").GetString().Should().Be("graph");

        var back = await c.PutAsJsonAsync($"{Flows}/big", Flow("big", s.Done, 5, s.Group, s.Amount, 1000));

        back.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(back)).GetProperty("errors").GetProperty("definition.shape")[0].GetString().Should().Be("這條流程已是流程圖格式，不能改回關卡格式");
        (await JsonOf(await c.GetAsync($"{Flows}/big"))).GetProperty("definition").GetProperty("shape").GetString().Should().Be("graph");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Toggle_GraphFlow_SameDefinition_Ok()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        (await c.PostAsJsonAsync($"{Flows}?module={s.Key}", AnyJoinBody("graph-one", s, "all"))).EnsureSuccessStatusCode();
        var flow = await JsonOf(await c.GetAsync($"{Flows}/graph-one"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(flow.GetRawText())!;
        node["active"] = false;

        var res = await c.PutAsync($"{Flows}/graph-one", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("active").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DraftPreview_Graph_PathReturned()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var res = await c.PostAsJsonAsync($"{Flows}/preview?module={s.Key}", new { draft = GraphBody("draft-graph", s, mode: "all"), initiatorUserId = "sales01" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("flowKey").GetString().Should().Be("draft-graph");
        body.GetProperty("path").GetProperty("nodeIds").EnumerateArray().Select(x => x.GetString()).Should().Contain("a");
    }

    /// <summary>已知例外（設計 §7.2）：資料庫裡已有的 graph 只改 active=false 可以存（不重跑驗證，讓設計者能先停掉引用壞掉的流程）；一改定義就被拒。</summary>
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Update_ExistingGraph_OnlyDeactivate_Allowed_DefinitionChange_Rejected()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var moduleId = db.Set<Module>().Single(m => m.Key == s.Key).Id;
            var flow = new ApprovalFlow
            {
                Key = "legacy-graph", NameI18n = new([KeyValuePair.Create("zh-TW", "既有流程圖")]), ModuleId = moduleId, ToOptionId = s.Done, Priority = 9,
                Active = true, CreatedUserId = "admin", CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0)
            };
            db.Add(flow);
            db.SaveForTests();
            var version = new ApprovalFlowVersion
            {
                FlowId = flow.Id, Version = 1, CreatedBy = "admin", CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0),
                Definition = new DynamicModules.Domain.Approvals.ApprovalDefinition("graph", null, null, null, [])
            };
            db.Add(version);
            db.SaveForTests();
            flow.CurrentVersionId = version.Id;
            db.SaveForTests();
        }

        object Body(bool active, string name) => new
        {
            key = "legacy-graph", name = Zh(name), toOptionId = s.Done, priority = 9, active,
            definition = new { shape = "graph", stages = Array.Empty<object>() }
        };
        var changed = await c.PutAsJsonAsync($"{Flows}/legacy-graph", new
        {
            key = "legacy-graph", name = Zh("既有流程圖"), toOptionId = s.Done, priority = 9, active = false,
            definition = new { shape = "graph", stages = Array.Empty<object>(), nodes = new[] { new { id = "start", kind = "start" } } }
        });
        changed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await c.PutAsJsonAsync($"{Flows}/legacy-graph", Body(false, "既有流程圖"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await JsonOf(ok)).GetProperty("active").GetBoolean().Should().BeFalse();
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

    // ───────── fg3a：草稿、乾跑驗證（J64、J65） ─────────

    private static object DraftBody(
        string key, (string Key, long Status, long Amount, string AmountKey, long Draft, long Done, long Group) s, string? baseRev = null,
        long? baseVersionId = null, bool force = false, int priority = 5, string approverMode = "any") => new
    {
        key, name = Zh(key), toOptionId = s.Done, priority, baseRev, baseVersionId, force,
        definition = new
        {
            shape = "graph", stages = Array.Empty<object>(),
            nodes = new object[]
            {
                new { id = "start", kind = "start" },
                new
                {
                    id = "a", kind = "approval", name = Zh("主管"), approvers = new { source = "group", groupIds = new[] { s.Group } },
                    mode = approverMode, editableFields = Array.Empty<long>(), onEmpty = "fallback"
                },
                new { id = "end", kind = "end" }
            },
            edges = new object[] { new { id = "e1", from = "start", to = "a" }, new { id = "e2", from = "a", to = "end" } }
        },
        layout = new { nodes = new { start = new { x = 0, y = 0 }, a = new { x = 200, y = 0 }, end = new { x = 400, y = 0 } } }
    };

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Draft_Routes_CamelCase_NullsOmitted()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);

        var created = await c.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", DraftBody("g-one", s));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await JsonOf(created);
        // API 的 JSON 省略 null：沒有版本的流程沒有 version、definition、versionId
        foreach (var name in new[] { "version", "definition", "versionId", "graph", "layout" }) body.TryGetProperty(name, out _).Should().BeFalse(name);
        body.GetProperty("active").GetBoolean().Should().BeFalse();
        var rev = body.GetProperty("draftRev").GetString()!;
        body.GetProperty("draft").GetProperty("definition").GetProperty("nodes")[1].GetProperty("approvers").GetProperty("source").GetString().Should().Be("group");
        body.GetProperty("draft").GetProperty("layout").GetProperty("nodes").GetProperty("a").GetProperty("x").GetDouble().Should().Be(200);
        body.GetProperty("draftUpdatedBy").GetString().Should().Be("admin");
        body.GetProperty("draftUpdatedByName").GetString().Should().NotBeNullOrEmpty();
        body.TryGetProperty("draftUpdatedTime", out _).Should().BeTrue();

        var saved = await c.PutAsJsonAsync($"{Flows}/g-one/draft", DraftBody("g-one", s, baseRev: rev, approverMode: "all"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await JsonOf(saved)).GetProperty("draftRev").GetString().Should().NotBe(rev);

        // 過期的權杖：409，本文帶 code，目前草稿的修改者、時間、權杖放在 params（前端的 toApiError 只留 code 與 params）
        var stale = await c.PutAsJsonAsync($"{Flows}/g-one/draft", DraftBody("g-one", s, baseRev: rev));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await JsonOf(stale);
        conflict.GetProperty("code").GetString().Should().Be("DraftConflict");
        var info = conflict.GetProperty("params");
        info.GetProperty("draftUpdatedBy").GetString().Should().Be("admin");
        info.GetProperty("draftUpdatedByName").GetString().Should().NotBeNullOrEmpty();
        info.TryGetProperty("draftUpdatedTime", out _).Should().BeTrue();
        info.GetProperty("draftRev").GetString().Should().NotBeNullOrEmpty();
        info.TryGetProperty("versionId", out _).Should().BeFalse("沒有版本的流程沒有 versionId");
        conflict.TryGetProperty("draftUpdatedByName", out _).Should().BeFalse("不展開到本文最上層");

        // 代碼重複：409 帶 code
        var dup = await c.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", DraftBody("g-one", s, priority: 6));
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(dup)).GetProperty("code").GetString().Should().Be("FlowKeyTaken");

        // 從未發布的流程不能捨棄草稿
        var discard = await c.DeleteAsync($"{Flows}/g-one/draft?baseRev={rev}");
        discard.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(discard)).GetProperty("code").GetString().Should().Be("DraftOnlyUseDelete");

        var one = await JsonOf(await c.GetAsync($"{Flows}/g-one"));
        one.GetProperty("draftRev").GetString().Should().NotBeNullOrEmpty();

        // 已發布的流程：捨棄草稿回到已發布的版本
        var big = await JsonOf(await c.GetAsync($"{Flows}/big"));
        var version = big.GetProperty("versionId").GetInt64();
        big.GetProperty("graph").GetProperty("shape").GetString().Should().Be("graph", "舊流程單筆回轉換後的直線圖");
        (await JsonOf(await c.GetAsync($"{Flows}?module={s.Key}"))).EnumerateArray().Select(f => f.TryGetProperty("graph", out var g) ? g : default)
            .Should().OnlyContain(g => g.ValueKind == System.Text.Json.JsonValueKind.Undefined, "清單端點不帶 graph");
        var bigDraft = await c.PutAsJsonAsync($"{Flows}/big/draft", DraftBody("big", s, baseVersionId: version, priority: 1));
        bigDraft.StatusCode.Should().Be(HttpStatusCode.OK, await bigDraft.Content.ReadAsStringAsync());
        var bigRev = (await JsonOf(bigDraft)).GetProperty("draftRev").GetString();
        var cleared = await c.DeleteAsync($"{Flows}/big/draft?baseRev={bigRev}");
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        var clearedBody = await JsonOf(cleared);
        clearedBody.TryGetProperty("draft", out _).Should().BeFalse();
        clearedBody.GetProperty("draftRev").GetString().Should().NotBe(bigRev, "捨棄後沒有草稿內容，但權杖換新（原地發布與捨棄不回到舊權杖）");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Publish_Response_HasWarningsArray()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        object Body(string? baseRev) => new
        {
            key = "g-warn", name = Zh("g-warn"), toOptionId = s.Done, priority = 5, baseRev,
            definition = new
            {
                shape = "graph", stages = Array.Empty<object>(),
                nodes = new object[]
                {
                    new { id = "start", kind = "start" },
                    new { id = "c", kind = "condition", name = Zh("金額") },
                    new
                    {
                        id = "a", kind = "approval", name = Zh("主管"), approvers = new { source = "group", groupIds = new[] { s.Group } },
                        mode = "any", editableFields = Array.Empty<long>(), onEmpty = "fallback"
                    },
                    new { id = "end", kind = "end" }
                },
                edges = new object[]
                {
                    new { id = "e0", from = "start", to = "c" },
                    new { id = "e1", from = "c", to = "a", when = new { all = new[] { new { field = s.Amount, op = "gte", value = 100 } } }, order = 1 },
                    new { id = "e2", from = "c", to = "end", @default = true },
                    new { id = "e3", from = "a", to = "end" }
                }
            }
        };
        var created = await c.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", Body(null));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var rev = (await JsonOf(created)).GetProperty("draftRev").GetString();

        var published = await c.PostAsJsonAsync($"{Flows}/g-warn/publish", Body(rev));

        published.StatusCode.Should().Be(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        var body = await JsonOf(published);
        body.GetProperty("flow").GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("flow").GetProperty("active").GetBoolean().Should().BeFalse();
        body.GetProperty("flow").TryGetProperty("draft", out _).Should().BeFalse("發布後沒有草稿內容");
        body.GetProperty("flow").GetProperty("draftRev").GetString().Should().NotBe(rev);
        var warnings = body.GetProperty("warnings");
        warnings.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        warnings.EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().Contain("W2");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Draft_ReadOnlyDesigner_403()
    {
        using var app = new TestApp();
        var s = await SeedApproval(app.AsUser());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Add(new UserProgramPrivilege { UserId = "sales01", ProgramCode = "DYN00000", CanRead = true });
            db.SaveForTests();
        }
        var reader = app.AsUser("sales01");

        (await reader.GetAsync($"{Flows}?module={s.Key}")).StatusCode.Should().Be(HttpStatusCode.OK, "唯讀設計者看得到流程");
        (await reader.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", DraftBody("g-one", s))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PutAsJsonAsync($"{Flows}/big/draft", DraftBody("big", s))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"{Flows}/big/draft?force=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsJsonAsync($"{Flows}/validate?module={s.Key}", DraftBody("g-one", s))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Draft_MasterModule_NonAdmin_403()
    {
        using var rig = new SharedMasterRig();
        await rig.ImportAllAsync();
        rig.Db(db => db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege
        {
            UserId = "planner01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        }));
        var designer = rig.App.AsUser("planner01");
        var body = new { key = "m-flow", name = Zh("x"), toOptionId = 1L, priority = 1, definition = new { shape = "graph", stages = Array.Empty<object>() } };

        (await designer.PostAsJsonAsync($"{Flows}/draft?module=master-bp-partner", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await designer.PostAsJsonAsync($"{Flows}/validate?module=master-bp-partner", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // 系統管理員過得了這一關（這個模組沒有狀態機，之後由流程自己的檢查擋下，不一定是哪個狀態碼）
        (await rig.Admin.PostAsJsonAsync($"{Flows}/draft?module=master-bp-partner", body)).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Flows_KeyedDraftValidateNew_RouteNormally()
    {
        // 代碼叫 draft、validate、new 的流程：字面路由段與 {key} 不衝突（不加保留字），涵蓋 GET、PUT、PUT draft、publish、validate
        using var app = new TestApp();
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var priority = 10;
        foreach (var key in new[] { "draft", "validate", "new" })
        {
            var created = await c.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", DraftBody(key, s, priority: priority++));
            created.StatusCode.Should().Be(HttpStatusCode.Created, key + await created.Content.ReadAsStringAsync());
            var rev = (await JsonOf(created)).GetProperty("draftRev").GetString();

            (await JsonOf(await c.GetAsync($"{Flows}/{key}"))).GetProperty("key").GetString().Should().Be(key);
            var saved = await c.PutAsJsonAsync($"{Flows}/{key}/draft", DraftBody(key, s, baseRev: rev, priority: priority));
            saved.StatusCode.Should().Be(HttpStatusCode.OK, key + await saved.Content.ReadAsStringAsync());
            (await c.PutAsJsonAsync($"{Flows}/{key}", Flow(key, s.Done, 77, s.Group))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "沒有版本的 PUT 要啟用回 400（J64）");
            var savedRev = (await JsonOf(saved)).GetProperty("draftRev").GetString();
            var published = await c.PostAsJsonAsync($"{Flows}/{key}/publish", DraftBody(key, s, baseRev: savedRev, priority: priority));
            published.StatusCode.Should().Be(HttpStatusCode.OK, key + await published.Content.ReadAsStringAsync());
            (await JsonOf(published)).GetProperty("flow").GetProperty("key").GetString().Should().Be(key);
            var validated = await c.PostAsJsonAsync($"{Flows}/validate?module={s.Key}&key={key}", DraftBody(key, s, priority: priority));
            validated.StatusCode.Should().Be(HttpStatusCode.OK, key + await validated.Content.ReadAsStringAsync());
            (await JsonOf(validated)).GetProperty("errors").EnumerateObject().Should().BeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task SaveDraft_Concurrent_OneWinsOneConflicts()
    {
        // 兩個請求讀到同一個 rev、同時存：恰一個 200、一個 409（SQLite 上寫入由 WriteGate 排隊，只有 PostgreSQL 的結果算數）
        using var app = new TestApp(fileDatabase: true);
        var c = app.AsUser();
        var s = await SeedApproval(c);
        var created = await c.PostAsJsonAsync($"{Flows}/draft?module={s.Key}", DraftBody("g-one", s));
        var rev = (await JsonOf(created)).GetProperty("draftRev").GetString();

        var results = await Task.WhenAll(
            app.AsUser().PutAsJsonAsync($"{Flows}/g-one/draft", DraftBody("g-one", s, baseRev: rev, approverMode: "all")),
            app.AsUser().PutAsJsonAsync($"{Flows}/g-one/draft", DraftBody("g-one", s, baseRev: rev, approverMode: "any")));

        results.Select(r => r.StatusCode).Order().Should().Equal(HttpStatusCode.OK, HttpStatusCode.Conflict);
    }
}
