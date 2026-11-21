using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// MCP 送簽測試共用的簽核造模（寫法照 <c>ApprovalApiTests.Arrange</c>）：狀態欄位「草稿 → 已核准」需簽核、一個一關的流程，
/// 簽核人是 sales 群組（sales01、sales02）或簽；<c>pick</c> 改成發起人從 sales 群組自選一人。<c>minAmount</c> 給了就只有金額 ≥ 它的
/// 記錄符合流程；<c>noMatch</c> 是沒有流程符合時擋下或直接轉移。資料全是虛構。
/// </summary>
internal static class McpApprovalKit
{
    public const string StageCode = "lead";

    public sealed record Rig(string Module, string Title, long StatusId, long Draft, long Done, string AmountKey, long FlowId);

    /// <param name="module">給了就在這個既有模組上加欄位與流程（呼叫端負責啟用與授權）；沒給就新建、啟用並授權給業務群組。</param>
    public static async Task<Rig> ArrangeAsync(TestApp app, HttpClient admin, string? module = null, string noMatch = ApprovalNoMatch.Block,
        bool pick = false, decimal? minAmount = null)
    {
        var key = module ?? await CreateModule(admin);
        var status = await AddField(admin, key, "enum");
        var amount = await AddField(admin, key, "decimal", new { scale = 2 });
        async Task<long> Option(string label) =>
            (await JsonOf(await admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = Zh(label), color = (string?)null })))
            .GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var done = await Option("已核准");
        if (module is null)
        {
            await Activate(admin, key);
            app.GrantBusinessGroups(key);
        }
        var sales = (await JsonOf(await admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        object Workflow(bool approval) => new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done } },
            transitions = new[]
            {
                new { from = draft, to = done, groupIds = Array.Empty<long>(), approval = approval ? new { required = true, noMatch } : null },
            },
        };
        (await admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(false))).EnsureSuccessStatusCode();
        object approvers = pick
            ? new { source = ApprovalSources.InitiatorPick, candidateGroupIds = new[] { sales }, min = 1, max = 1 }
            : new { source = ApprovalSources.Group, groupIds = new[] { sales } };
        var flow = await admin.PostAsJsonAsync($"{Base}/approval-flows?module={key}", new
        {
            key = "f-" + key, name = Zh("流程"), toOptionId = done, priority = 1, active = true,
            definition = new
            {
                shape = ApprovalShapes.Sequence,
                when = minAmount is { } min ? new { all = new[] { new { field = amount.Id, op = "gte", value = min } } } : null,
                stages = new[]
                {
                    new
                    {
                        code = StageCode, name = Zh("業務"), approvers, mode = ApprovalModes.Any, editableFields = Array.Empty<long>(),
                        dueHours = (int?)24, onEmpty = ApprovalOnEmpty.Fallback,
                    },
                },
            },
        });
        flow.StatusCode.Should().Be(HttpStatusCode.Created, await flow.Content.ReadAsStringAsync());
        var flowId = McpTestKit.Query(app, db => db.Set<ApprovalFlow>().Single(f => f.Key == "f-" + key).Id);
        (await admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", Workflow(true))).EnsureSuccessStatusCode();
        return new Rig(key, await TitleKey(admin, key), status.Id, draft, done, amount.Key, flowId);
    }

    public static async Task<(long Id, string RowVersion)> NewRecord(HttpClient c, Rig r, string title = "單據", decimal amount = 1,
        Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?> { [r.Title] = title, [r.AmountKey] = amount };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var res = await c.PostAsJsonAsync($"{Base}/modules/{r.Module}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var created = await JsonOf(res);
        return (created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    public static Task<HttpResponseMessage> RestSubmit(HttpClient c, Rig r, long id, string rowVersion, object? picks = null) =>
        c.PostAsJsonAsync($"{Base}/records/{id}/approval/submit", new { to = r.Done, recordRowVersion = rowVersion, picks });

    /// <summary>進行中的實例（GET records/{id}/approval 的 current）。</summary>
    public static async Task<JsonElement> Current(HttpClient c, long recordId)
    {
        var res = await c.GetAsync($"{Base}/records/{recordId}/approval");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("current");
    }

    /// <summary>簽核的對照形式：實例、關卡、簽核任務、簽核歷程（動作與行為者）、通知（收件人與種類）。</summary>
    public static string Shape(TestApp app, long recordId) => McpTestKit.Query(app, db =>
    {
        var instances = db.Set<ApprovalInstance>().Where(i => i.RecordId == recordId).OrderBy(i => i.Id).ToList();
        var lines = new List<string>();
        foreach (var i in instances)
        {
            lines.Add($"instance {i.Status}|{i.RouteKind}|{i.FlowId}|{i.FromOptionId}->{i.ToOptionId}|{i.InitiatorUserId}|{i.TitleSnapshot}");
            lines.AddRange(db.Set<ApprovalStageRun>().Where(s => s.InstanceId == i.Id).OrderBy(s => s.Seq).AsEnumerable()
                .Select(s => $"stage {s.Seq}|{s.StageCode}|{s.Status}"));
            lines.Add("tasks " + string.Join(",", db.Set<ApprovalTask>().Where(t => t.InstanceId == i.Id).AsEnumerable()
                .Select(t => $"{t.AssigneeUserId}/{t.Status}").Order(StringComparer.Ordinal)));
            lines.Add("actions " + string.Join(",", db.Set<ApprovalAction>().Where(a => a.InstanceId == i.Id).OrderBy(a => a.Id).AsEnumerable()
                .Select(a => $"{a.Action}/{a.ActorUserId}/{a.Comment}")));
            lines.Add("notices " + string.Join(",", db.Set<Notification>().Where(n => n.SourceType == "approval" && n.SourceId == i.Id).AsEnumerable()
                .Select(n => $"{n.UserId}/{n.Kind}").Order(StringComparer.Ordinal)));
        }
        return string.Join("\n", lines);
    });

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };
}
