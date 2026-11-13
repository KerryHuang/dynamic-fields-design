using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 簽核執行期經 HTTP（簽核設計 §8.4、§15；計畫 Review Focus 5）：檔案型 SQLite，送出、同意、撤回真正同時到達時只有一個成功；
/// 冪等重送回放；簽核狀態、簽核中心端點。
/// </summary>
public class ApprovalApiTests : IDisposable
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public ApprovalApiTests()
    {
        _app = new TestApp(fileDatabase: true);
        _app.Settings["Logging:LogLevel:Default"] = "Warning";
        _admin = _app.AsUser();
    }

    public void Dispose() => _app.Dispose();

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private sealed record Rig(string Module, string Title, long Draft, long Done, string? Amount = null);

    /// <summary>草稿 → 已核准 需簽核（block）；流程一關，群組 sales（sales01、sales02）或簽。</summary>
    private async Task<Rig> Arrange(string stageGroup = "sales", bool lockAmount = false)
    {
        var key = await CreateModule(_admin);
        var status = await AddField(_admin, key, "enum");
        var amount = lockAmount ? await AddField(_admin, key, "decimal") : default;
        async Task<long> Option(string label) =>
            (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = Zh(label), color = (string?)null })))
            .GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var done = await Option("已核准");
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var sales = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == stageGroup).GetProperty("id").GetInt64();
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
                shape = "sequence",
                when = lockAmount ? new { all = new[] { new { field = amount.Id, op = "gte", value = 0 } } } : null,
                stages = new[]
                {
                    new
                    {
                        code = "lead", name = new Dictionary<string, string> { ["zh-TW"] = "業務", ["en-US"] = "Sales" }, approvers = new { source = "group", groupIds = new[] { sales } },
                        mode = "any", editableFields = Array.Empty<long>(), dueHours = (int?)24, onEmpty = "fallback"
                    }
                }
            }
        });
        flow.StatusCode.Should().Be(HttpStatusCode.Created, await flow.Content.ReadAsStringAsync());
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(true))).EnsureSuccessStatusCode();
        return new Rig(key, await TitleKey(_admin, key), draft, done, lockAmount ? amount.Key : null);
    }

    private async Task<(long Id, string RowVersion)> NewRecord(Rig r)
    {
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/records",
            new { values = new Dictionary<string, object> { [r.Title] = "單據" } }));
        return (created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    private Task<HttpResponseMessage> Submit(HttpClient c, Rig r, long id, string rowVersion) =>
        c.PostAsJsonAsync($"{Base}/records/{id}/approval/submit", new { to = r.Done, recordRowVersion = rowVersion });

    private async Task<long> Submitted(Rig r)
    {
        var (id, rv) = await NewRecord(r);
        var res = await Submit(_admin, r, id, rv);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return id;
    }

    private async Task<JsonElement> Current(HttpClient c, long recordId)
    {
        var res = await c.GetAsync($"{Base}/records/{recordId}/approval");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("current");
    }

    private static long TaskOf(JsonElement current, string user) =>
        current.GetProperty("stages")[0].GetProperty("tasks").EnumerateArray()
            .Single(t => t.GetProperty("assigneeUserId").GetString() == user).GetProperty("id").GetInt64();

    private Task<HttpResponseMessage> SubmitIn(string locale, Rig r, long id, string rowVersion, long? to = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/records/{id}/approval/submit")
        {
            Content = JsonContent.Create(new { to = to ?? r.Done, recordRowVersion = rowVersion })
        };
        req.Headers.Add("X-Locale", locale);
        return _admin.SendAsync(req);
    }

    private static async Task<string?> Message(HttpResponseMessage res) =>
        (await JsonOf(res)).TryGetProperty("message", out var m) ? m.GetString() : null;

    [Fact]
    public async Task SubmitTwice_Concurrent_201And409()
    {
        var r = await Arrange();
        var (id, rv) = await NewRecord(r);
        var results = await Task.WhenAll(Submit(_app.AsUser(), r, id, rv), Submit(_app.AsUser(), r, id, rv));
        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await Message(results.Single(x => x.StatusCode == HttpStatusCode.Conflict))).Should().Be("這張單已在簽核中");
    }

    [Fact]
    public async Task AnyStage_TwoApprove_Concurrent_200And409()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var s1 = _app.AsUser("sales01");
        var s2 = _app.AsUser("sales02");
        var current = await Current(s1, id);
        var rv = current.GetProperty("rowVersion").GetString();
        var todo = await JsonOf(await s1.GetAsync($"{Base}/approval-tasks?box=todo&module={r.Module}"));
        todo.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("recordId").GetInt64()).Should().Equal(id);

        var results = await Task.WhenAll(
            s1.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve", new { rowVersion = rv }),
            s2.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales02")}/approve", new { rowVersion = rv }));
        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await Message(results.Single(x => x.StatusCode == HttpStatusCode.Conflict))).Should().Be("這一關已由他人處理");
    }

    [Fact]
    public async Task WithdrawAndApprove_Concurrent_OneWins()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        var rv = current.GetProperty("rowVersion").GetString();
        var instanceId = current.GetProperty("id").GetInt64();

        var results = await Task.WhenAll(
            _app.AsUser().PostAsJsonAsync($"{Base}/approval-instances/{instanceId}/withdraw", new { rowVersion = rv }),
            s1.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve", new { rowVersion = rv }));
        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);

        var after = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}/approval"));
        after.TryGetProperty("current", out _).Should().BeFalse();   // null 不輸出
        var finished = after.GetProperty("previous").EnumerateArray().Single();
        var actions = finished.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        var withdrawWon = results[0].StatusCode == HttpStatusCode.OK;
        finished.GetProperty("status").GetString().Should().Be(withdrawWon ? "withdrawn" : "approved");
        // 不會既撤回又往下推進
        (actions.Contains("withdraw") && actions.Contains("approve")).Should().BeFalse(string.Join(",", actions));
        var record = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        record.GetProperty("approval").GetProperty("status").GetString().Should().Be(withdrawWon ? "withdrawn" : "approved");
    }

    [Fact]
    public async Task Approve_IdempotencyKeyReplay_OneEffect()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        var body = new { rowVersion = current.GetProperty("rowVersion").GetString() };
        HttpRequestMessage Request()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve") { Content = JsonContent.Create(body) };
            req.Headers.Add("Idempotency-Key", "approve-once-1");
            return req;
        }

        var first = await s1.SendAsync(Request());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        first.Headers.Contains("Idempotent-Replay").Should().BeFalse();
        var second = await s1.SendAsync(Request());
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replay").Should().ContainSingle("true");
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());

        var finished = (await JsonOf(await _admin.GetAsync($"{Base}/records/{id}/approval"))).GetProperty("previous").EnumerateArray().Single();
        finished.GetProperty("actions").EnumerateArray().Count(a => a.GetProperty("action").GetString() == "approve").Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Transfer_IdempotencyReplay_OneNewTask()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        var tasksBefore = current.GetProperty("stages")[0].GetProperty("tasks").GetArrayLength();
        var body = new { rowVersion = current.GetProperty("rowVersion").GetString(), userId = "floor01", comment = "請代為處理" };
        HttpRequestMessage Request()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/approval-tasks/{TaskOf(current, "sales01")}/transfer") { Content = JsonContent.Create(body) };
            req.Headers.Add("Idempotency-Key", "transfer-once-1");
            return req;
        }

        var first = await s1.SendAsync(Request());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await JsonOf(first)).GetProperty("outcome").GetString().Should().Be("transferred");
        var second = await s1.SendAsync(Request());
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replay").Should().ContainSingle("true");
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());

        var tasks = (await Current(_admin, id)).GetProperty("stages")[0].GetProperty("tasks").EnumerateArray().ToList();
        tasks.Count(t => t.GetProperty("assigneeUserId").GetString() == "floor01").Should().Be(1);
        tasks.Count.Should().Be(tasksBefore + 1);   // 只多一個新任務
        tasks.Single(t => t.GetProperty("assigneeUserId").GetString() == "sales01").GetProperty("status").GetString().Should().Be("transferred");
        var finished = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}/approval"));
        finished.GetProperty("current").GetProperty("actions").EnumerateArray()
            .Count(a => a.GetProperty("action").GetString() == "transfer").Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task TransferAndApprove_Concurrent_OneWins()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        var rv = current.GetProperty("rowVersion").GetString();
        var taskId = TaskOf(current, "sales01");

        var results = await Task.WhenAll(
            s1.PostAsJsonAsync($"{Base}/approval-tasks/{taskId}/transfer", new { rowVersion = rv, userId = "floor01" }),
            _app.AsUser("sales01").PostAsJsonAsync($"{Base}/approval-tasks/{taskId}/approve", new { rowVersion = rv }));
        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);

        var record = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}/approval"));
        var view = record.TryGetProperty("current", out var c) ? c : record.GetProperty("previous").EnumerateArray().Single();
        var actions = view.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        var transferWon = results[0].StatusCode == HttpStatusCode.OK;
        // 不會既轉交又同意
        (actions.Contains("transfer") && actions.Contains("approve")).Should().BeFalse(string.Join(",", actions));
        actions.Contains(transferWon ? "transfer" : "approve").Should().BeTrue(string.Join(",", actions));
        view.GetProperty("status").GetString().Should().Be(transferWon ? "pending" : "approved");
    }

    [Fact]
    public async Task EnUs_submit_blocked_when_no_approver_is_found()
    {
        // 關卡群組只有發起人自己（admin），備援（其他系統管理員）也沒有人
        var r = await Arrange("mis");
        var (id, rv) = await NewRecord(r);
        var res = await SubmitIn("en-US", r, id, rv);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await Message(res)).Should().Be("No approver found for stage \"Sales\" from source \"MIS\", and there is no fallback either");
        // 沒帶標頭仍是繁中
        var zh = await Submit(_admin, r, id, rv);
        (await Message(zh)).Should().Be("關卡「業務」的來源「MIS」找不到簽核人，備援也沒有人");
    }

    [Fact]
    public async Task EnUs_submit_blocked_when_already_pending_or_transition_not_allowed()
    {
        var r = await Arrange();
        var (id, rv) = await NewRecord(r);
        var notAllowed = await SubmitIn("en-US", r, id, rv, r.Draft);
        notAllowed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await notAllowed.Content.ReadAsStringAsync());
        (await JsonOf(notAllowed)).GetProperty("errors").EnumerateObject().Single().Value[0].GetString().Should().Be("This status change is not allowed");

        (await SubmitIn("en-US", r, id, rv)).StatusCode.Should().Be(HttpStatusCode.Created);
        var again = await SubmitIn("en-US", r, id, rv);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Message(again)).Should().Be("This record is already in approval");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Put_LockedField_Returns400ProblemWithFieldKey()
    {
        // 流程條件引用「金額」，所以核准後金額鎖定；備註（標題）不鎖
        var r = await Arrange(lockAmount: true);
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/records",
            new { values = new Dictionary<string, object> { [r.Title] = "單據", [r.Amount!] = 100 } }));
        var id = created.GetProperty("id").GetInt64();
        (await Submit(_admin, r, id, created.GetProperty("rowVersion").GetString()!)).StatusCode.Should().Be(HttpStatusCode.Created);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        (await s1.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        var res = await _admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [r.Amount!] = 999 }, rowVersion = record.GetProperty("rowVersion").GetString()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.GetProperty(r.Amount!)[0].GetString().Should().Contain("這個欄位已經過簽核");
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Upload_LockedRecord_Returns409_NoFile()
    {
        var r = await Arrange(lockAmount: true);
        var files = await AddField(_admin, r.Module, "attachment");
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/records",
            new { values = new Dictionary<string, object> { [r.Title] = "單據", [r.Amount!] = 100 } }));
        var id = created.GetProperty("id").GetInt64();
        (await Submit(_admin, r, id, created.GetProperty("rowVersion").GetString()!)).StatusCode.Should().Be(HttpStatusCode.Created);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        (await s1.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);

        var file = new ByteArrayContent("hello"u8.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var res = await _admin.PostAsync($"{Base}/records/{id}/attachments/{files.Id}",
            new MultipartFormDataContent { { file, "file", "a.txt" } });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("附件不能再增減");
        var record = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        (record.GetProperty("values").TryGetProperty(files.Key, out var list) ? list.GetArrayLength() : 0).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task GetRecord_Json_HasApprovalLockAndGrant()
    {
        var r = await Arrange(lockAmount: true);
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/records",
            new { values = new Dictionary<string, object> { [r.Title] = "單據", [r.Amount!] = 100 } }));
        var id = created.GetProperty("id").GetInt64();
        (await Submit(_admin, r, id, created.GetProperty("rowVersion").GetString()!)).StatusCode.Should().Be(HttpStatusCode.Created);
        var s1 = _app.AsUser("sales01");
        var current = await Current(s1, id);
        (await s1.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));

        var lck = record.GetProperty("approvalLock");
        lck.GetProperty("fieldKeys").EnumerateArray().Select(e => e.GetString()).Should().Contain(r.Amount);
        lck.GetProperty("detailRowsLocked").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        record.GetProperty("approvalGrant").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task GetSchema_GrantPath_Returns200_AndStaleETagGets403NotModifiedAfterTaskEnds()
    {
        var r = await Arrange();
        var id = await Submitted(r);
        var sales = _app.AsUser("sales01");
        var withRead = await sales.GetAsync($"{Base}/modules/{r.Module}/schema");
        withRead.StatusCode.Should().Be(HttpStatusCode.OK);
        var readTag = withRead.Headers.ETag!.ToString();
        RevokeBusinessGroups(r.Module);

        (await sales.GetAsync($"{Base}/modules/{r.Module}/schema")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var grant = await sales.GetAsync($"{Base}/modules/{r.Module}/schema?recordId={id}");

        grant.StatusCode.Should().Be(HttpStatusCode.OK, await grant.Content.ReadAsStringAsync());
        grant.Headers.ETag!.ToString().Should().NotBe(readTag);   // 只驗形狀：收權限前後的權限內容本來就不同
        (await JsonOf(grant)).GetProperty("permissions").GetProperty("edit").GetBoolean().Should().BeFalse();
        var grantTag = grant.Headers.ETag!.ToString();
        (await JsonOf(await sales.GetAsync($"{Base}/records/{id}"))).GetProperty("approvalGrant").GetBoolean().Should().BeTrue();

        // 任務結束後，瀏覽器帶著舊的 grant ETag 重新驗證：要回 403，不是 304（否則前端會繼續顯示 grant schema）
        var current = await Current(sales, id);
        (await sales.PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(current, "sales01")}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modules/{r.Module}/schema?recordId={id}");
        stale.Headers.TryAddWithoutValidation("If-None-Match", grantTag);
        (await sales.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void RevokeBusinessGroups(string moduleKey)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var code = db.Set<Module>().Single(m => m.Key == moduleKey).ProgramCode;
        db.Set<GroupProgramPrivilege>().RemoveRange(db.Set<GroupProgramPrivilege>().Where(g => g.ProgramCode == code));
        db.SaveChanges();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Inbox_StatusQuery_FiltersAndJsonHasEditableKeysShape()
    {
        var r = await Arrange();
        var id = await Submitted(r);

        var current = await Current(_admin, id);
        current.GetProperty("titleSnapshot").GetString().Should().Be("單據");
        current.GetProperty("stages")[0].GetProperty("editableFieldKeys").GetArrayLength().Should().Be(0);

        var pending = await JsonOf(await _admin.GetAsync($"{Base}/approval-tasks?box=submitted&status=pending"));
        pending.GetProperty("totalCount").GetInt32().Should().Be(1);
        var rejected = await JsonOf(await _admin.GetAsync($"{Base}/approval-tasks?box=submitted&status=rejected"));
        rejected.GetProperty("totalCount").GetInt32().Should().Be(0);
        var bad = await _admin.GetAsync($"{Base}/approval-tasks?box=submitted&status=x");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
