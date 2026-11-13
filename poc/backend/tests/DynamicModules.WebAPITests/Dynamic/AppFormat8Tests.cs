using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套件格式 8 的簽核流程隨應用程式開啟（簽核設計 §14.4、§17.4 第 1、3 步、§17.5 審查第 1 項）：流程建成版本 1、轉移帶需簽核設定、
/// 略過的群組由備援頂替並逐條列出、前置檢查與整批回滾。
/// 套件與組織在測試內定義：群組 purchaseLead → 進銷存管理組（ops01）、board → 業務主管（manager01）、buyers → 採購組（buyer01）。
/// </summary>
[Trait("Category", "Postgres")]
public class AppFormat8Tests : IDisposable
{
    private const string AppId = "t8", PoKey = "t8-po", Large = "t8-po-large", Mid = "t8-po-mid";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private const string Perm = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }""";

    /// <summary>採購單：草稿 → 已確認 需簽核（direct）；大額（≥ 100000）兩關、一萬以上一關，以下直接確認。</summary>
    private static string Package(int formatVersion = 8, string? flows = null, string? events = null)
    {
        flows ??= """
            [ { "code": "t8-po-large", "name": { "zh-TW": "採購單（大額）" }, "active": true,
                "module": "po", "to": "confirmed", "priority": 1, "shape": "sequence",
                "when": { "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] },
                "rejectTo": "draft",
                "fallbackApprovers": { "source": "group", "groups": ["purchaseLead"] },
                "stages": [
                  { "code": "lead", "name": { "zh-TW": "採購主管" }, "approvers": { "source": "group", "groups": ["purchaseLead"] },
                    "mode": "any", "dueHours": 24, "onEmpty": "fallback" },
                  { "code": "board", "name": { "zh-TW": "董事會" }, "approvers": { "source": "group", "groups": ["board", "buyers"] },
                    "mode": "all", "dueHours": 48, "onEmpty": "skip" } ] },
              { "code": "t8-po-mid", "name": { "zh-TW": "採購單（一萬以上）" }, "active": true,
                "module": "po", "to": "confirmed", "priority": 2, "shape": "sequence",
                "when": { "all": [ { "field": "po.total", "op": "gte", "value": 10000 } ] },
                "stages": [
                  { "code": "lead", "name": { "zh-TW": "採購主管" }, "approvers": { "source": "group", "groups": ["purchaseLead"] },
                    "mode": "any", "editableFields": ["po.memo"] } ] } ]
            """;
        var approval = formatVersion >= 8 ? """, "approval": { "required": true, "noMatch": "direct" }""" : "";
        var editable = formatVersion >= 8 ? """, "editable": true""" : "";
        var flowSection = formatVersion >= 8 ? $$""", "approvalFlows": {{flows}}""" : "";
        return $$"""
            {
              "format": "formwright-app", "formatVersion": {{formatVersion}},
              "manifest": { "id": "t8", "version": "1.0.0", "name": { "zh-TW": "簽核套件測試" } },
              "groups": [
                { "code": "purchaseLead", "name": { "zh-TW": "採購主管" } },
                { "code": "board", "name": { "zh-TW": "董事會" } },
                { "code": "buyers", "name": { "zh-TW": "採購人員" } }
              ],
              "modules": [
                { "code": "po", "key": "t8-po", "name": { "zh-TW": "採購單" }, "kind": "standalone", "titleField": "title",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                    { "code": "title", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                    { "code": "total", "type": "decimal", "label": { "zh-TW": "總額" }, "config": { "scale": 0 } },
                    { "code": "memo", "type": "text", "label": { "zh-TW": "備註" }, "config": {} },
                    { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                      "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } }, { "code": "confirmed", "label": { "zh-TW": "已確認" } },
                                   { "code": "cancelled", "label": { "zh-TW": "已取消" } } ] }
                  ] } ],
                  "permissions": [ { "group": "purchaseLead", "scope": "all", "privileges": {{Perm}} },
                                   { "group": "board", "scope": "all", "privileges": {{Perm}} },
                                   { "group": "buyers", "scope": "all", "privileges": {{Perm}} } ] },
                { "code": "note", "key": "t8-note", "name": { "zh-TW": "採購備忘" }, "kind": "standalone", "titleField": "subject",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "內容" }, "fields": [
                    { "code": "subject", "type": "text", "label": { "zh-TW": "主旨" }, "config": { "required": true } },
                    { "code": "po", "type": "reference", "label": { "zh-TW": "採購單" }, "config": { "refTarget": { "kind": "module", "key": "po" } } }
                  ] } ],
                  "permissions": [] }
              ],
              "workflows": [
                { "module": "po", "statusField": "status", "initial": "draft",
                  "states": [ { "option": "draft" }, { "option": "confirmed", "readOnly": true }, { "option": "cancelled"{{editable}} } ],
                  "transitions": [
                    { "from": "draft", "to": "confirmed", "groups": []{{approval}} },
                    { "from": "draft", "to": "cancelled", "groups": [] },
                    { "from": "confirmed", "to": "cancelled", "groups": [] },
                    { "from": "cancelled", "to": "draft", "groups": [] } ] }
              ]{{flowSection}}{{(events is null ? "" : $", \"events\": {events}")}}
            }
            """;
    }

    private object Body(params string[] skipped) => Body(skipped, null);

    private object Body(string[] skipped, string[]? excludeFields)
    {
        var map = new Dictionary<string, string> { ["purchaseLead"] = "ops-manager", ["board"] = "sales-manager", ["buyers"] = "purchasing" };
        var groupMap = map.ToDictionary(kv => kv.Key, kv => skipped.Contains(kv.Key)
            ? (object)new { mode = "skip" }
            : new { mode = "map", groupId = _rig.GroupId(kv.Value) });
        return new { groupMap, excludeFields = excludeFields ?? [] };
    }

    private async Task<JsonElement> ImportOpenAsync(string? json = null, params string[] skipped)
    {
        await _rig.ImportAsync(json ?? Package());
        return await _rig.OpenOkAsync(AppId, Body(skipped));
    }

    private async Task<JsonElement> PreviewAsync(object body)
    {
        var res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/{AppId}/preview", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<List<JsonElement>> FlowsAsync()
    {
        var res = await _rig.Admin.GetAsync($"{Base}/approval-flows?module={PoKey}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).EnumerateArray().ToList();
    }

    private long Option(string code) => _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == $"po.{code}").OptionId);

    private void AssertNothingCreated(int existingFlows = 0)
    {
        _rig.Db(db => db.Set<App>().Any(a => a.Key == AppId)).Should().BeFalse("App 列回滾");
        _rig.Db(db => db.Set<Module>().Any(m => m.Key == PoKey)).Should().BeFalse("模組回滾");
        _rig.Db(db => db.Set<ApprovalFlow>().Count()).Should().Be(existingFlows, "流程回滾");
    }

    private async Task<(long Id, string RowVersion)> NewPoAsync(decimal total)
    {
        var created = await _rig.CreateRecordAsync(_rig.Admin, PoKey, new Dictionary<string, object>
        {
            [_rig.FieldKey(AppId, "po.title")] = $"PO-{total}", [_rig.FieldKey(AppId, "po.total")] = total
        });
        return (created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    private async Task<HttpResponseMessage> SubmitAsync(decimal total)
    {
        var (id, rv) = await NewPoAsync(total);
        return await _rig.Admin.PostAsJsonAsync($"{Base}/records/{id}/approval/submit", new { to = Option("confirmed"), recordRowVersion = rv });
    }

    private async Task<JsonElement> CurrentAsync(long recordId)
    {
        var res = await _rig.Admin.GetAsync($"{Base}/records/{recordId}/approval");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task ApproveAsync(string user, long recordId)
    {
        var current = (await CurrentAsync(recordId)).GetProperty("current");
        var task = current.GetProperty("stages").EnumerateArray().SelectMany(s => s.GetProperty("tasks").EnumerateArray())
            .First(t => t.GetProperty("status").GetString() == ApprovalTaskStatus.Open && t.GetProperty("assigneeUserId").GetString() == user);
        var res = await _rig.App.AsUser(user).PostAsJsonAsync($"{Base}/approval-tasks/{task.GetProperty("id").GetInt64()}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync() + _rig.LastError());
    }

    [Fact]
    public async Task OpenApp_CreatesFlowsAtVersion1_AndTransitionApproval()
    {
        var opened = await ImportOpenAsync();
        opened.GetProperty("formatVersion").GetInt32().Should().Be(8);
        opened.GetProperty("approvalSubstitutions").EnumerateArray().Should().BeEmpty();

        var flows = await FlowsAsync();
        flows.Select(f => f.GetProperty("key").GetString()).Should().Equal(Large, Mid);
        flows.Select(f => f.GetProperty("version").GetInt32()).Should().Equal(1, 1);
        flows.Select(f => f.GetProperty("priority").GetInt32()).Should().Equal(1, 2);
        flows[0].GetProperty("toOptionId").GetInt64().Should().Be(Option("confirmed"));
        var large = flows[0].GetProperty("definition");
        large.GetProperty("rejectTo").GetInt64().Should().Be(Option("draft"));
        large.GetProperty("stages")[0].GetProperty("approvers").GetProperty("groupIds")[0].GetInt64().Should().Be(_rig.GroupId("ops-manager"));
        large.GetProperty("stages")[0].GetProperty("dueHours").GetInt32().Should().Be(24);
        flows[1].GetProperty("definition").GetProperty("stages")[0].GetProperty("editableFields")[0].GetInt64()
            .Should().Be(_rig.FieldId(AppId, "po.memo"));

        var wf = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{PoKey}/workflow"));
        var draftToConfirmed = wf.GetProperty("transitions").EnumerateArray()
            .Single(t => t.GetProperty("from").GetInt64() == Option("draft") && t.GetProperty("to").GetInt64() == Option("confirmed"));
        draftToConfirmed.GetProperty("approval").GetProperty("required").GetBoolean().Should().BeTrue();
        draftToConfirmed.GetProperty("approval").GetProperty("noMatch").GetString().Should().Be("direct");
        wf.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("optionId").GetInt64() == Option("cancelled"))
            .GetProperty("editable").GetBoolean().Should().BeTrue();
        wf.GetProperty("lockedStates").EnumerateArray().Select(s => s.GetInt64()).Should().Contain(Option("confirmed"));
    }

    [Fact]
    public async Task OpenApp_SmallDirect_LargeUsesFirstFlow()
    {
        await ImportOpenAsync();
        var small = await SubmitAsync(5000);
        small.StatusCode.Should().Be(HttpStatusCode.OK, await small.Content.ReadAsStringAsync());
        _rig.Db(db => db.Set<ApprovalInstance>().Count()).Should().Be(0, "沒有流程符合、入口是 direct：直接確認");

        var large = await SubmitAsync(150000);
        large.StatusCode.Should().Be(HttpStatusCode.Created, await large.Content.ReadAsStringAsync());
        var flowId = _rig.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == Large).Id);
        _rig.Db(db => db.Set<ApprovalInstance>().Single().FlowId).Should().Be(flowId);
    }

    [Fact]
    public async Task Preview_SkippedGroup_ListsSubstitutions_OpenKeepsFlow()
    {
        await _rig.ImportAsync(Package());
        var preview = await PreviewAsync(Body("board"));
        var substitution = preview.GetProperty("approvalSubstitutions").EnumerateArray().Single();
        substitution.GetProperty("flow").GetString().Should().Be(Large);
        substitution.GetProperty("stage").GetString().Should().Be("board");
        substitution.GetProperty("group").GetString().Should().Be("board");
        substitution.GetProperty("kind").GetString().Should().Be("approvers");
        substitution.GetProperty("replacedBy").GetString().Should().Be("flowFallback");
        const string warning = "簽核流程「採購單（大額）」關卡「董事會」的簽核人來源「董事會」已略過，改由流程的備援簽核人頂替";
        preview.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(warning);

        var opened = await _rig.OpenOkAsync(AppId, Body("board"));
        opened.GetProperty("approvalSubstitutions").EnumerateArray().Should().ContainSingle();
        opened.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(warning);
        var flows = await FlowsAsync();
        flows.Should().HaveCount(2, "流程一律保留");
        var board = flows[0].GetProperty("definition").GetProperty("stages")[1].GetProperty("approvers");
        board.GetProperty("unmatchedGroups").EnumerateArray().Select(g => g.GetString()).Should().Equal("董事會");
        board.GetProperty("groupIds").EnumerateArray().Select(g => g.GetInt64()).Should().Equal(_rig.GroupId("purchasing"));

        // 會簽仍要湊滿原本的票數：第 2 關是 buyer01 與一位備援簽核人（ops01），只有 buyer01 同意不算完成
        var submitted = await SubmitAsync(150000);
        submitted.StatusCode.Should().Be(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var recordId = _rig.Db(db => db.Set<ApprovalInstance>().Single().RecordId);
        await ApproveAsync("ops01", recordId);
        var stage2 = (await CurrentAsync(recordId)).GetProperty("current").GetProperty("stages")[1].GetProperty("tasks").EnumerateArray().ToList();
        stage2.Select(t => t.GetProperty("assigneeUserId").GetString()).Should().BeEquivalentTo(["buyer01", "ops01"]);
        await ApproveAsync("buyer01", recordId);
        _rig.Db(db => db.Set<ApprovalInstance>().Single().Status).Should().Be(ApprovalInstanceStatus.Pending, "備援席位還沒同意");
        await ApproveAsync("ops01", recordId);
        _rig.Db(db => db.Set<ApprovalInstance>().Single().Status).Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Open_SkippedGroup_OnEmptySkip_StoredAsFallback()
    {
        await ImportOpenAsync(null, "board");
        var stage = (await FlowsAsync())[0].GetProperty("definition").GetProperty("stages")[1];
        stage.GetProperty("onEmpty").GetString().Should().Be("fallback", "略過群組不能變成少簽");
    }

    [Fact]
    public async Task Open_ExcludeFieldUsedByFlow_400()
    {
        await _rig.ImportAsync(Package());
        var res = await _rig.OpenAsync(AppId, Body([], ["po.total"]));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("「總額」是簽核流程「採購單（大額）」需要的欄位，不能排除");
        AssertNothingCreated();
        (await JsonOf(await _rig.Admin.GetAsync(SharedMasterRig.Apps))).EnumerateArray().Should().NotContain(a => a.GetProperty("key").GetString() == AppId);

        var status = await _rig.OpenAsync(AppId, Body([], ["po.status"]));
        status.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await status.Content.ReadAsStringAsync()).Should().Contain("「狀態」是簽核流程");
    }

    [Fact]
    public async Task Open_FlowCodeTaken_Rejected()
    {
        // 先在另一個模組手動建一個同代碼的流程
        var other = await CreateModule(_rig.Admin);
        var status = await AddField(_rig.Admin, other, "enum");
        async Task<long> Opt(string label) =>
            (await JsonOf(await _rig.Admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options",
                new { label = new Dictionary<string, string> { ["zh-TW"] = label }, color = (string?)null }))).GetProperty("id").GetInt64();
        var draft = await Opt("草稿");
        var done = await Opt("完成");
        await Activate(_rig.Admin, other);
        (await _rig.Admin.PutAsJsonAsync($"{Base}/modules/{other}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done } },
            transitions = new[] { new { from = draft, to = done, groupIds = Array.Empty<long>() } }
        })).EnsureSuccessStatusCode();
        var created = await _rig.Admin.PostAsJsonAsync($"{Base}/approval-flows?module={other}", new
        {
            key = Mid, name = new Dictionary<string, string> { ["zh-TW"] = "既有流程" }, toOptionId = done, priority = 1, active = true,
            definition = new
            {
                shape = "sequence",
                stages = new[] { new { code = "mgr", name = new Dictionary<string, string> { ["zh-TW"] = "主管" },
                    approvers = new { source = "manager", level = 1 }, mode = "any", editableFields = Array.Empty<long>(), onEmpty = "fallback" } }
            }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        await _rig.ImportAsync(Package());
        (await PreviewAsync(Body())).GetProperty("errors").EnumerateArray().Select(e => e.GetString())
            .Should().Contain($"approvalFlows.{Mid}: 流程代碼已被使用");
        (await _rig.OpenAsync(AppId, Body())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNothingCreated(existingFlows: 1);
    }

    [Fact]
    public async Task Open_StagesOverSystemPolicy_PreviewErrorAndNothingCreated()
    {
        await _rig.ImportAsync(Package());
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/approval.maxStages", new { value = 1 })).EnsureSuccessStatusCode();
        (await PreviewAsync(Body())).GetProperty("errors").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("approvalFlows[t8-po-large].stages: 關卡必須有 1 到 1 關");
        (await _rig.OpenAsync(AppId, Body())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Open_DesignerValidationFails_RollsBackEverything()
    {
        // 事件以 update 把採購單狀態直接設成需簽核的「已確認」（§5.3）：V8 不解析事件，由第二趟存狀態機擋下
        const string events = """
            [ { "code": "t8-note-confirms", "name": { "zh-TW": "備忘確認採購單" }, "active": true, "source": "note",
                "trigger": { "on": "create" }, "when": null,
                "effects": [ { "code": "confirm", "kind": "update", "target": "po", "via": "note.po",
                               "set": [ { "to": "po.status", "const": "opt_confirmed" } ] } ] } ]
            """;
        await _rig.ImportAsync(Package(events: events));
        var res = await _rig.OpenAsync(AppId, Body());
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Should().Contain(k => k.StartsWith("po.workflow"), errors.GetRawText());
        AssertNothingCreated();
        _rig.Db(db => db.Set<ModuleEvent>().Any(e => e.Key == "t8-note-confirms")).Should().BeFalse("事件回滾");
    }

    [Fact]
    public async Task Import_Format8_ThenOpen_Works()
    {
        await ImportOpenAsync();
        _rig.Db(db => db.Set<App>().Single(a => a.Key == AppId).FormatVersion).Should().Be(8);
    }

    [Fact]
    public async Task Import_Format9_Rejected()
    {
        var json = Package().Replace("\"formatVersion\": 8", "\"formatVersion\": 9");
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("較新版本");
    }

    [Fact]
    public async Task Open_Format7PackageWithoutApproval_Unchanged()
    {
        var opened = await ImportOpenAsync(Package(formatVersion: 7));
        opened.GetProperty("formatVersion").GetInt32().Should().Be(7);
        _rig.Db(db => db.Set<ApprovalFlow>().Count()).Should().Be(0);
        var wf = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{PoKey}/workflow"));
        wf.GetProperty("transitions").EnumerateArray().Select(t => t.TryGetProperty("approval", out var a) ? a.ValueKind : JsonValueKind.Null)
            .Should().OnlyContain(k => k == JsonValueKind.Null);
    }
}
