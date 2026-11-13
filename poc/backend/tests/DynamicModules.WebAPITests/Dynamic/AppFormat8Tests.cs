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
/// 略過的群組由備援頂替並逐條列出、前置檢查與整批回滾；匯出目前版本、往返到空資料庫（§17.4 第 11 步）、匯出失敗的情況（計畫 U7）。
/// 套件與組織在測試內定義：群組 purchaseLead → 進銷存管理組（ops01）、board → 業務主管（manager01）、buyers → 採購組（buyer01）。
/// </summary>
[Trait("Category", "Postgres")]
public class AppFormat8Tests : IDisposable
{
    private const string AppId = "t8", PoKey = "t8-po", Large = "t8-po-large", Mid = "t8-po-mid";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private const string Perm = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }""";

    private const string DefaultFlows = """
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

    /// <summary>採購單：草稿 → 已確認 需簽核（direct）；大額（≥ 100000）兩關、一萬以上一關，以下直接確認。</summary>
    private static string Package(int formatVersion = 8, string? flows = null, string? events = null)
    {
        flows ??= DefaultFlows;
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
                    { "code": "owner", "type": "user", "label": { "zh-TW": "負責人" }, "config": {} },
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

    /// <summary>流程 A 的第 1 關只有 board 一個群組；第 2 關 userField 的候選群組只有 board。</summary>
    private const string SingleGroupFlows = """
        [ { "code": "t8-po-large", "name": { "zh-TW": "採購單（大額）" }, "active": true,
            "module": "po", "to": "confirmed", "priority": 1, "shape": "sequence",
            "when": { "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] },
            "rejectTo": "draft",
            "fallbackApprovers": { "source": "group", "groups": ["purchaseLead"] },
            "stages": [
              { "code": "board", "name": { "zh-TW": "董事會" }, "approvers": { "source": "group", "groups": ["board"] },
                "mode": "any", "onEmpty": "skip" },
              { "code": "owner", "name": { "zh-TW": "負責人" }, "approvers": { "source": "userField", "field": "po.owner", "candidateGroups": ["board"] },
                "mode": "any" } ] } ]
        """;

    [Fact]
    public async Task Open_SkipOnlyGroupOfStage_KeepsFlow_FallbackApproves()
    {
        // 審查 M1：一關的群組全部被略過，預覽與開啟要一致：流程保留、該關整個交給備援（§14.4），不是開啟才 400
        await _rig.ImportAsync(Package(flows: SingleGroupFlows));
        var preview = await PreviewAsync(Body("board"));
        preview.GetProperty("canOpen").GetBoolean().Should().BeTrue();
        preview.GetProperty("approvalSubstitutions").EnumerateArray().Select(x => x.GetProperty("kind").GetString())
            .Should().Equal("approvers", "candidates");

        await _rig.OpenOkAsync(AppId, Body("board"));
        var stages = (await FlowsAsync()).Single().GetProperty("definition").GetProperty("stages");
        stages[0].GetProperty("approvers").GetProperty("unmatchedGroups").EnumerateArray().Select(g => g.GetString()).Should().Equal("董事會");
        stages[0].GetProperty("onEmpty").GetString().Should().Be("fallback");
        stages[1].GetProperty("approvers").GetProperty("candidatesUnmatched").GetBoolean().Should().BeTrue();

        var submitted = await SubmitAsync(150000);
        submitted.StatusCode.Should().Be(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var recordId = _rig.Db(db => db.Set<ApprovalInstance>().Single().RecordId);
        var first = (await CurrentAsync(recordId)).GetProperty("current").GetProperty("stages")[0].GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("assigneeUserId").GetString()).ToList();
        first.Should().Equal(["ops01"], "第 1 關整個由流程的備援簽核人頂替");
        await ApproveAsync("ops01", recordId);
        await ApproveAsync("ops01", recordId);
        _rig.Db(db => db.Set<ApprovalInstance>().Single().Status).Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Preview_SkippedRejectTransition_ErrorMatchesOpen()
    {
        // 審查 S1：退回用的轉移（草稿 → 已取消）只開給 buyers，略過 buyers 後這條轉移不建，流程退回不了：預覽就要擋，與開啟一致
        var flows = SingleGroupFlows.Replace("\"rejectTo\": \"draft\"", "\"rejectTo\": \"cancelled\"");
        var json = Package(flows: flows).Replace("""{ "from": "draft", "to": "cancelled", "groups": [] }""", """{ "from": "draft", "to": "cancelled", "groups": ["buyers"] }""");
        await _rig.ImportAsync(json);
        var preview = await PreviewAsync(Body("buyers"));
        preview.GetProperty("canOpen").GetBoolean().Should().BeFalse();
        const string error = "groupMap.buyers: 略過後「草稿 → 已取消」沒有任何群組，簽核流程「採購單（大額）」無法退回，請對應這個群組";
        preview.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Should().Contain(error);
        var res = await _rig.OpenAsync(AppId, Body("buyers"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain(error);
        AssertNothingCreated();

        await _rig.OpenOkAsync(AppId, Body());
    }

    [Fact]
    public async Task Import_StagesOverSystemPolicy_400()
    {
        // 審查 N2：匯入時就依系統層生效值檢查
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/approval.maxStages", new { value = 1 })).EnsureSuccessStatusCode();
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import", new StringContent(Package(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("approvalFlows[t8-po-large].stages: 關卡必須有 1 到 1 關");
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

    // ---- 匯出（§14.4、計畫 J10、U7） ----

    /// <summary>開啟套件的群組對應到的既有群組，匯出時以群組 Key 當代號（ops-manager → ops_manager）。</summary>
    private static readonly Dictionary<string, string> ExportedGroups = new()
    {
        ["ops_manager"] = "ops-manager", ["sales_manager"] = "sales-manager", ["purchasing"] = "purchasing"
    };

    private static object BodyIn(SharedMasterRig rig) => new
    {
        groupMap = ExportedGroups.ToDictionary(kv => kv.Key, kv => (object)new { mode = "map", groupId = rig.GroupId(kv.Value) }),
        excludeFields = Array.Empty<string>()
    };

    private async Task<HttpResponseMessage> ExportRawAsync() => await _rig.Admin.GetAsync($"{SharedMasterRig.Apps}/{AppId}/export");

    /// <summary>以設計器的 PUT 改流程定義（取目前的回應，改 definition 後存回）。</summary>
    private async Task UpdateFlowAsync(string key, Action<JsonNode> change)
    {
        var flow = JsonNode.Parse((await JsonOf(await _rig.Admin.GetAsync($"{Base}/approval-flows/{key}"))).GetRawText())!;
        var definition = flow["definition"]!;
        change(definition);
        var res = await _rig.Admin.PutAsJsonAsync($"{Base}/approval-flows/{key}", new JsonObject
        {
            ["key"] = key, ["name"] = flow["name"]!.DeepClone(), ["toOptionId"] = flow["toOptionId"]!.DeepClone(),
            ["priority"] = flow["priority"]!.DeepClone(), ["active"] = flow["active"]!.DeepClone(), ["definition"] = definition.DeepClone()
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_WritesFormat8_FlowsTransitionsEditable()
    {
        await ImportOpenAsync();
        var (package, _) = await _rig.ExportAsync(AppId);
        package.GetProperty("formatVersion").GetInt32().Should().Be(8);
        var flows = package.GetProperty("approvalFlows").EnumerateArray().ToList();
        flows.Select(f => f.GetProperty("code").GetString()).Should().Equal(Large, Mid);
        var large = flows[0];
        large.GetProperty("shape").GetString().Should().Be("sequence");
        large.GetProperty("module").GetString().Should().Be("po");
        large.GetProperty("to").GetString().Should().Be("confirmed");
        large.GetProperty("rejectTo").GetString().Should().Be("draft");
        large.GetProperty("when").GetProperty("all")[0].GetProperty("field").GetString().Should().Be("po.total");
        large.GetProperty("fallbackApprovers").GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().Equal("ops_manager");
        large.GetProperty("stages")[1].GetProperty("approvers").GetProperty("groups").EnumerateArray().Select(g => g.GetString())
            .Should().Equal("sales_manager", "purchasing");
        large.GetProperty("stages")[1].GetProperty("onEmpty").GetString().Should().Be("skip");
        flows[1].GetProperty("stages")[0].GetProperty("editableFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("po.memo");
        package.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("code").GetString())
            .Should().Contain(["ops_manager", "sales_manager", "purchasing"]);

        var wf = JsonNode.Parse(package.GetRawText())!["workflows"]![0]!;
        var transitions = wf["transitions"]!.AsArray();
        var approval = transitions.Single(t => (string?)t!["from"] == "draft" && (string?)t!["to"] == "confirmed")!["approval"]!;
        ((bool)approval["required"]!).Should().BeTrue();
        ((string?)approval["noMatch"]).Should().Be("direct");
        transitions.Count(t => t!["approval"] is not null).Should().Be(1);
        var states = wf["states"]!.AsArray();
        ((bool?)states.Single(st => (string?)st!["option"] == "cancelled")!["editable"]).Should().BeTrue();
        states.Where(st => (string?)st!["option"] != "cancelled").Should().OnlyContain(st => st!["editable"] == null, "沒標可編輯的狀態不多寫屬性");
    }

    /// <summary>第三個流程掛在沒有需簽核轉移進入的「已取消」上（設計器允許、套件只警告，主控裁決 S2），而且是停用的。</summary>
    private const string SpareFlow = """
        { "code": "t8-po-spare", "name": { "zh-TW": "採購單取消（備用）" }, "active": false,
          "module": "po", "to": "cancelled", "priority": 1, "shape": "sequence",
          "stages": [ { "code": "lead", "name": { "zh-TW": "採購主管" }, "approvers": { "source": "manager", "level": 1 }, "mode": "any" } ] }
        """;

    private const string SpareWarning = "approvalFlows[t8-po-spare].to: 狀態「已取消」沒有需簽核的轉移進入，這個流程不會被用到";

    [Fact]
    public async Task ExportThenImportIntoEmptyDb_FlowsAndApprovalSurvive()
    {
        var flows = DefaultFlows.TrimEnd();
        await ImportOpenAsync(Package(flows: flows[..^1] + ", " + SpareFlow + " ]"));
        var (package, warnings) = await _rig.ExportAsync(AppId);
        warnings.Should().NotContain(w => w.Contains("簽核"));
        var original = await FlowsAsync();

        // 換一個資料庫：匯入、開啟，流程（版本 1）、需簽核設定、可編輯狀態都一樣；備用流程照開並列出警告（複審 N-b）
        using var other = new SharedMasterRig();
        await other.ImportAsync(package.GetRawText());
        var preview = await JsonOf(await other.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/{AppId}/preview", BodyIn(other)));
        preview.GetProperty("canOpen").GetBoolean().Should().BeTrue(preview.GetRawText());
        var opened = await other.OpenOkAsync(AppId, BodyIn(other));
        opened.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(SpareWarning);
        opened.GetProperty("approvalSubstitutions").EnumerateArray().Should().BeEmpty();

        var res = await other.Admin.GetAsync($"{Base}/approval-flows?module={PoKey}");
        var reopened = (await JsonOf(res)).EnumerateArray().ToList();
        reopened.Select(f => f.GetProperty("key").GetString()).Should().Equal(original.Select(f => f.GetProperty("key").GetString()));
        reopened.Select(f => f.GetProperty("version").GetInt32()).Should().OnlyContain(v => v == 1);
        reopened.Select(f => f.GetProperty("active").GetBoolean()).Should().Equal(original.Select(f => f.GetProperty("active").GetBoolean()));
        long OtherOption(string code) => other.Db(db => db.Set<AppOption>().Single(o => o.Name == $"po.{code}").OptionId);
        foreach (var (a, b) in original.Zip(reopened))
        {
            b.GetProperty("priority").GetInt32().Should().Be(a.GetProperty("priority").GetInt32());
            var stagesA = a.GetProperty("definition").GetProperty("stages").EnumerateArray().ToList();
            var stagesB = b.GetProperty("definition").GetProperty("stages").EnumerateArray().ToList();
            foreach (var name in new[] { "code", "name", "mode", "dueHours", "onEmpty" })
                stagesB.Select(x => x.TryGetProperty(name, out var v) ? v.GetRawText() : null)
                    .Should().Equal(stagesA.Select(x => x.TryGetProperty(name, out var v) ? v.GetRawText() : null), name);
        }
        reopened[0].GetProperty("toOptionId").GetInt64().Should().Be(OtherOption("confirmed"));
        reopened[0].GetProperty("definition").GetProperty("rejectTo").GetInt64().Should().Be(OtherOption("draft"));
        reopened[0].GetProperty("definition").GetProperty("stages")[1].GetProperty("approvers").GetProperty("groupIds").EnumerateArray()
            .Select(g => g.GetInt64()).Should().Equal(other.GroupId("sales-manager"), other.GroupId("purchasing"));
        reopened[2].GetProperty("toOptionId").GetInt64().Should().Be(OtherOption("cancelled"));

        var wf = await JsonOf(await other.Admin.GetAsync($"{Base}/modules/{PoKey}/workflow"));
        var approvals = wf.GetProperty("transitions").EnumerateArray().Where(t => t.TryGetProperty("approval", out var x) && x.ValueKind == JsonValueKind.Object).ToList();
        approvals.Should().ContainSingle();
        approvals[0].GetProperty("to").GetInt64().Should().Be(OtherOption("confirmed"));
        approvals[0].GetProperty("approval").GetProperty("noMatch").GetString().Should().Be("direct");
        wf.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("optionId").GetInt64() == OtherOption("cancelled"))
            .GetProperty("editable").GetBoolean().Should().BeTrue();

        // 送出 150000 走同一個流程（優先序 1 的大額）
        var created = await other.CreateRecordAsync(other.Admin, PoKey, new Dictionary<string, object>
        {
            [other.FieldKey(AppId, "po.title")] = "PO-RT", [other.FieldKey(AppId, "po.total")] = 150000m
        });
        var submit = await other.Admin.PostAsJsonAsync($"{Base}/records/{created.GetProperty("id").GetInt64()}/approval/submit",
            new { to = OtherOption("confirmed"), recordRowVersion = created.GetProperty("rowVersion").GetString() });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        var flowId = other.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == Large).Id);
        other.Db(db => db.Set<ApprovalInstance>().Single().FlowId).Should().Be(flowId);
    }

    [Fact]
    public async Task Export_ExportsCurrentVersionOnly()
    {
        await ImportOpenAsync();
        (await SubmitAsync(150000)).StatusCode.Should().Be(HttpStatusCode.Created);
        await UpdateFlowAsync(Large, d => d["stages"]![0]!["dueHours"] = 72);
        (await FlowsAsync())[0].GetProperty("version").GetInt32().Should().Be(2, "用過的流程改定義產生新版本");

        var (package, _) = await _rig.ExportAsync(AppId);
        package.GetProperty("approvalFlows")[0].GetProperty("stages")[0].GetProperty("dueHours").GetInt32().Should().Be(72);

        using var other = new SharedMasterRig();
        await other.ImportAsync(package.GetRawText());
        await other.OpenOkAsync(AppId, BodyIn(other));
        var flow = (await JsonOf(await other.Admin.GetAsync($"{Base}/approval-flows/{Large}")));
        flow.GetProperty("version").GetInt32().Should().Be(1);
        flow.GetProperty("definition").GetProperty("stages")[0].GetProperty("dueHours").GetInt32().Should().Be(72);
    }

    [Fact]
    public async Task Export_UsersSource_409_NamesFlowAndStage()
    {
        await ImportOpenAsync();
        await UpdateFlowAsync(Large, d => d["stages"]![0]!["approvers"] = new JsonObject
        {
            ["source"] = "users", ["userIds"] = new JsonArray("buyer01")
        });
        var res = await ExportRawAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("簽核流程「採購單（大額）」關卡「採購主管」指定了具體使用者，帳號屬於這個資料庫，套件不收");
    }

    [Fact]
    public async Task Export_UnmatchedUnits_409()
    {
        await ImportOpenAsync(null, "board");
        var res = await ExportRawAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should()
            .Contain("簽核流程「採購單（大額）」關卡「董事會」有開啟套件時略過的群組，請先在設計器移除或補上群組");
    }

    [Fact]
    public async Task Export_WithoutApproval_StillWorks()
    {
        await ImportOpenAsync(Package(formatVersion: 7));
        var (package, _) = await _rig.ExportAsync(AppId);
        package.GetProperty("formatVersion").GetInt32().Should().Be(8);
        if (package.TryGetProperty("approvalFlows", out var flows)) flows.EnumerateArray().Should().BeEmpty();
        JsonNode.Parse(package.GetRawText())!["workflows"]![0]!["transitions"]!.AsArray()
            .Should().OnlyContain(t => t!["approval"] == null);
    }
}
