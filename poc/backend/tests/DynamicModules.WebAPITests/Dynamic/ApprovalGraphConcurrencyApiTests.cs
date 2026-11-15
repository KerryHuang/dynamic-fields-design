using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 流程圖實例的併發經 HTTP（計畫 fg2 Task 10；J16、J17、J18）：檔案型 SQLite，同一張單的動作真正同時到達。
/// SQLite 上寫入請求被 WriteGate 整筆排隊，這裡驗的是「排隊之後結果正確」（沒有 409 誤擋、匯合只推進一次、加簽總數不超過上限），
/// 不是交錯提交；真正的交錯提交與重試只有 PostgreSQL 驗得到，所以整個類別標 Postgres（TestApp 目前寫死 SQLite，pg0 補 provider 切換前是「未驗證」）。
/// 流程圖版本由測試直接寫進資料庫（API 到 fg2g2 才收 graph），擋門以 <see cref="TestApp.ExtraServices"/> 換成開啟版。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalGraphConcurrencyApiTests : IDisposable
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public ApprovalGraphConcurrencyApiTests()
    {
        _app = new TestApp(fileDatabase: true) { ExtraServices = s => s.Replace(ServiceDescriptor.Singleton<ApprovalShapeGate, OpenShapeGate>()) };
        _app.Settings["Logging:LogLevel:Default"] = "Warning";
        _admin = _app.AsUser();
    }

    public void Dispose() => _app.Dispose();

    private sealed class OpenShapeGate : ApprovalShapeGate
    {
        public override bool Open => true;
    }

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    // ───────── 流程圖定義（簽核人是示範使用者 sales01、sales02、floor01） ─────────

    private static I18nText Name(string id) => new([KeyValuePair.Create("zh-TW", id)]);

    private static ApprovalNodeDef Approver(string id, params string[] users) =>
        new(id, ApprovalNodeKinds.Approval, Name(id), Approvers: new ApproverSource(ApprovalSources.Users, UserIds: [.. users]),
            Mode: ApprovalModes.Any, EditableFields: [], OnEmpty: ApprovalOnEmpty.Fallback);

    private static ApprovalDefinition Graph(string joinMode, params string[] aUsers) => new(ApprovalShapes.Graph, null, null, null, [],
        [new("start", ApprovalNodeKinds.Start), new("f", ApprovalNodeKinds.Fork, Name("f")), Approver("a", aUsers),
         Approver("b", "sales02"), new("j", ApprovalNodeKinds.Join, ForkId: "f", Mode: joinMode), Approver("c", "floor01"), new("end", ApprovalNodeKinds.End)],
        [new("e0", "start", "f"), new("fa", "f", "a"), new("fb", "f", "b"), new("aj", "a", "j"), new("bj", "b", "j"), new("jc", "j", "c"), new("ce", "c", "end")]);

    private sealed record Rig(string Module, string Title, long Draft, long Done);

    /// <summary>草稿 → 已核准 需簽核（block）；流程先以一關的 sequence 存檔，再把目前版本的定義換成流程圖。</summary>
    private async Task<Rig> Arrange(ApprovalDefinition graph)
    {
        var key = await CreateModule(_admin);
        var status = await AddField(_admin, key, "enum");
        async Task<long> Option(string label) =>
            (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = Zh(label), color = (string?)null })))
            .GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var done = await Option("已核准");
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var sales = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        object Workflow(bool approval) => new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done } },
            transitions = new[]
            {
                new { from = draft, to = done, groupIds = Array.Empty<long>(), approval = approval ? new { required = true, noMatch = "block" } : null }
            }
        };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(false))).EnsureSuccessStatusCode();
        var flow = await _admin.PostAsJsonAsync($"{Base}/approval-flows?module={key}", new
        {
            key = "f-" + key, name = Zh("流程"), toOptionId = done, priority = 1, active = true,
            definition = new
            {
                shape = "sequence", when = (object?)null,
                stages = new[]
                {
                    new
                    {
                        code = "lead", name = Zh("業務"), approvers = new { source = "group", groupIds = new[] { sales } },
                        mode = "any", editableFields = Array.Empty<long>(), dueHours = (int?)24, onEmpty = "fallback"
                    }
                }
            }
        });
        flow.StatusCode.Should().Be(HttpStatusCode.Created, await flow.Content.ReadAsStringAsync());
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(true))).EnsureSuccessStatusCode();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var flowId = db.Set<ApprovalFlow>().Single(f => f.Key == "f-" + key).Id;
            db.Set<ApprovalFlowVersion>().Single(v => v.FlowId == flowId).Definition = graph;
            await db.SaveChangesAsync();
        }
        return new Rig(key, await TitleKey(_admin, key), draft, done);
    }

    private async Task<long> Submitted(Rig r)
    {
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/records",
            new { values = new Dictionary<string, object> { [r.Title] = "單據" } }));
        var id = created.GetProperty("id").GetInt64();
        var res = await _admin.PostAsJsonAsync($"{Base}/records/{id}/approval/submit",
            new { to = r.Done, recordRowVersion = created.GetProperty("rowVersion").GetString() });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return id;
    }

    private async Task<JsonElement> Current(long recordId)
    {
        var res = await _admin.GetAsync($"{Base}/records/{recordId}/approval");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("current");
    }

    private static IEnumerable<JsonElement> StagesOf(JsonElement current, string nodeId) =>
        current.GetProperty("stages").EnumerateArray().Where(s => s.GetProperty("nodeId").GetString() == nodeId);

    private static long TaskOf(JsonElement current, string user) =>
        current.GetProperty("stages").EnumerateArray().SelectMany(s => s.GetProperty("tasks").EnumerateArray())
            .Single(t => t.GetProperty("assigneeUserId").GetString() == user && t.GetProperty("status").GetString() == "open").GetProperty("id").GetInt64();

    private static string RowVersion(JsonElement current) => current.GetProperty("rowVersion").GetString()!;

    private Task<HttpResponseMessage> ApproveAs(string user, long taskId, string rowVersion) =>
        _app.AsUser(user).PostAsJsonAsync($"{Base}/approval-tasks/{taskId}/approve", new { rowVersion });

    private static async Task<string?> Message(HttpResponseMessage res) =>
        (await JsonOf(res)).TryGetProperty("message", out var m) ? m.GetString() : null;

    // ───────── 併發 ─────────

    [Fact]
    public async Task TwoBranchesApproveAtOnce_BothSucceed()
    {
        // 兩條分支同時同意，帶的都是同一個（其中一個已過期的）畫面版本：任務層動作不比對版本，兩個都 200，沒有 409
        var id = await Submitted(await Arrange(Graph(ApprovalModes.All, "sales01")));
        var current = await Current(id);
        var rv = RowVersion(current);

        var results = await Task.WhenAll(ApproveAs("sales01", TaskOf(current, "sales01"), rv), ApproveAs("sales02", TaskOf(current, "sales02"), rv));

        results.Select(x => x.StatusCode).Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK);
    }

    [Fact]
    public async Task TwoBranchesReachAllJoinAtOnce_AdvancesExactlyOnce()
    {
        // 兩條分支同時到達全部到齊的匯合：匯合後的節點恰好一列、只有一組任務
        var id = await Submitted(await Arrange(Graph(ApprovalModes.All, "sales01")));
        var current = await Current(id);
        var rv = RowVersion(current);

        var results = await Task.WhenAll(ApproveAs("sales01", TaskOf(current, "sales01"), rv), ApproveAs("sales02", TaskOf(current, "sales02"), rv));

        results.Select(x => x.StatusCode).Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK);
        var after = await Current(id);
        var c = StagesOf(after, "c").ToList();
        c.Should().ContainSingle();
        c[0].GetProperty("tasks").EnumerateArray().Should().ContainSingle().Which.GetProperty("assigneeUserId").GetString().Should().Be("floor01");
    }

    [Fact]
    public async Task OrSignTwoApproversAtOnce_OneSucceeds_Other409StageHandled()
    {
        var id = await Submitted(await Arrange(Graph(ApprovalModes.All, "sales01", "floor02")));
        var current = await Current(id);
        var rv = RowVersion(current);

        var results = await Task.WhenAll(ApproveAs("sales01", TaskOf(current, "sales01"), rv), ApproveAs("floor02", TaskOf(current, "floor02"), rv));

        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await Message(results.Single(x => x.StatusCode == HttpStatusCode.Conflict))).Should().Be("這一關已由他人處理");
    }

    [Fact]
    public async Task HolderAndDelegateApproveAtOnce_OneSucceeds_Other409()
    {
        // sales01 把代理設給 sales02：sales01 的任務在送出時就派給代理人，本人與代理人都能處理；兩人同時同意同一個任務，只有一個成功
        var today = DateOnly.FromDateTime(DateTime.Now);
        (await _app.AsUser("sales01").PutAsJsonAsync($"{Base}/me/approval-delegate",
            new { delegateUserId = "sales02", startDate = today, endDate = today.AddDays(1) })).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await Submitted(await Arrange(Graph(ApprovalModes.All, "sales01")));
        var current = await Current(id);
        var rv = RowVersion(current);
        var delegated = current.GetProperty("stages").EnumerateArray().Where(s => s.GetProperty("nodeId").GetString() == "a")
            .SelectMany(s => s.GetProperty("tasks").EnumerateArray()).Single(t => t.GetProperty("originalUserId").GetString() == "sales01");
        delegated.GetProperty("assigneeUserId").GetString().Should().Be("sales02");
        var taskId = delegated.GetProperty("id").GetInt64();

        var results = await Task.WhenAll(ApproveAs("sales01", taskId, rv), ApproveAs("sales02", taskId, rv));

        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await Message(results.Single(x => x.StatusCode == HttpStatusCode.Conflict))).Should().Be("這一關已由他人處理");
    }

    [Fact]
    public async Task AnyJoinTwoBranchesAtOnce_OneArrival_OtherBranchCancelled()
    {
        // 任一到齊：兩條分支同時同意，只有第一個到達匯合、取消另一條；晚到的看到「其他分支已完成」，匯合後的節點只啟動一次
        var id = await Submitted(await Arrange(Graph(ApprovalModes.Any, "sales01")));
        var current = await Current(id);
        var rv = RowVersion(current);

        var results = await Task.WhenAll(ApproveAs("sales01", TaskOf(current, "sales01"), rv), ApproveAs("sales02", TaskOf(current, "sales02"), rv));

        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await Message(results.Single(x => x.StatusCode == HttpStatusCode.Conflict))).Should().Be("其他分支已完成，不需處理");
        var after = await Current(id);
        new[] { "a", "b" }.SelectMany(n => StagesOf(after, n)).Select(s => s.GetProperty("status").GetString()).Order()
            .Should().Equal("approved", "closed");
        StagesOf(after, "c").Should().ContainSingle();
    }

    [Fact]
    public async Task TwoBranchesAddSignAtOnce_TotalNeverExceedsLimit()
    {
        // 加簽關卡數上限 1：兩條分支同時加簽，總數不會超過上限（一個 200、一個 400）
        var r = await Arrange(Graph(ApprovalModes.All, "sales01"));
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Add(new ModulePolicy
            {
                ModuleId = db.Set<Module>().Single(m => m.Key == r.Module).Id, Code = PolicyCatalog.ApprovalMaxAddSignStages.Code, Value = "1", ModifiedUserId = "admin"
            });
            await db.SaveChangesAsync();
        }
        var id = await Submitted(r);
        var current = await Current(id);
        var rv = RowVersion(current);

        Task<HttpResponseMessage> AddSign(string user, string to) => _app.AsUser(user)
            .PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, user)}/add-sign", new { rowVersion = rv, userIds = new[] { to }, mode = "any" });
        var results = await Task.WhenAll(AddSign("sales01", "floor01"), AddSign("sales02", "floor02"));

        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.BadRequest]);
        var after = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}/approval"));
        after.GetProperty("current").GetProperty("actions").EnumerateArray().Count(a => a.GetProperty("action").GetString() == "add-sign").Should().Be(1);
    }
}
