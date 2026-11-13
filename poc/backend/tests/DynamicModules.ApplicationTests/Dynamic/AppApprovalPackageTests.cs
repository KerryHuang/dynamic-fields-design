using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Policies;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 套件格式 8 的 approvalFlows[]、轉移的 approval、狀態的 editable：結構層、引用層與狀態一致性（簽核設計 §14）。純函式，不需要資料庫。
/// 模組「採購單」狀態 草稿／已確認／已取消／已退回，流程對照 §17.4（大額兩關、中額一關，以下直接確認）。
/// </summary>
public class AppApprovalPackageTests
{
    private const string LargeFlow = """
        { "code": "t8-po-large", "name": { "zh-TW": "採購單（大額）" }, "active": true,
          "module": "po", "to": "confirmed", "priority": 1, "shape": "sequence",
          "when": { "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] },
          "rejectTo": "draft",
          "fallbackApprovers": { "source": "group", "groups": ["purchaseLead"] },
          "stages": [
            { "code": "lead", "name": { "zh-TW": "採購主管" }, "approvers": { "source": "group", "groups": ["purchaseLead"] },
              "mode": "any", "editableFields": [], "dueHours": 24, "onEmpty": "fallback" },
            { "code": "board", "name": { "zh-TW": "董事會" }, "approvers": { "source": "group", "groups": ["board", "buyers"] },
              "mode": "all", "dueHours": 48 }
          ] }
        """;

    private const string MidFlow = """
        { "code": "t8-po-mid", "name": { "zh-TW": "採購單（一萬以上）" }, "active": true,
          "module": "po", "to": "confirmed", "priority": 2, "shape": "sequence",
          "when": { "all": [ { "field": "po.total", "op": "gte", "value": 10000 } ] },
          "stages": [
            { "code": "lead", "name": { "zh-TW": "直屬主管" }, "approvers": { "source": "manager", "level": 1 },
              "mode": "any", "editableFields": ["po.memo"], "onEmpty": "fallback" }
          ] }
        """;

    private const string Transitions = """
        { "from": "draft", "to": "confirmed", "groups": [], "approval": { "required": true, "noMatch": "direct" } },
        { "from": "draft", "to": "cancelled", "groups": [] },
        { "from": "confirmed", "to": "cancelled", "groups": [] },
        { "from": "cancelled", "to": "draft", "groups": [] }
        """;

    private const string States = """
        { "option": "draft" }, { "option": "confirmed", "readOnly": true }, { "option": "cancelled", "editable": true },
        { "option": "returned" }
        """;

    private static string Package(string flows, int formatVersion = 8, string transitions = Transitions, string states = States, string kind = "") => $$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "t8", "version": "1.0.0", "name": { "zh-TW": "簽核套件測試" }{{kind}} },
          "groups": [
            { "code": "purchaseLead", "name": { "zh-TW": "採購主管" } },
            { "code": "board", "name": { "zh-TW": "董事會" } },
            { "code": "buyers", "name": { "zh-TW": "採購人員" } }
          ],
          "modules": [
            { "code": "po", "key": "t8-po", "name": { "zh-TW": "採購單" }, "kind": "standalone", "titleField": "title",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "title", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "total", "type": "decimal", "label": { "zh-TW": "總額" }, "config": {} },
                { "code": "memo", "type": "text", "label": { "zh-TW": "備註" }, "config": {} },
                { "code": "buyer", "type": "user", "label": { "zh-TW": "採購人員" }, "config": {} },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } }, { "code": "confirmed", "label": { "zh-TW": "已確認" } },
                               { "code": "cancelled", "label": { "zh-TW": "已取消" } }, { "code": "returned", "label": { "zh-TW": "已退回" } } ] }
              ] } ],
              "permissions": [] },
            { "code": "vendor", "key": "t8-vendor", "name": { "zh-TW": "供應商" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                { "code": "rating", "type": "integer", "label": { "zh-TW": "評等" }, "config": {} },
                { "code": "state", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "new", "label": { "zh-TW": "新建" } }, { "code": "ok", "label": { "zh-TW": "合格" } } ] }
              ] } ],
              "permissions": [] },
            { "code": "line", "key": "t8-line", "name": { "zh-TW": "採購明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "明細" }, "fields": [
                { "code": "item", "type": "text", "label": { "zh-TW": "品名" }, "config": { "required": true } },
                { "code": "po", "type": "reference", "label": { "zh-TW": "採購單" },
                  "config": { "refTarget": { "kind": "module", "key": "po" }, "relation": "detail" } },
                { "code": "state", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "open", "label": { "zh-TW": "未結" } }, { "code": "closed", "label": { "zh-TW": "已結" } } ] }
              ] } ],
              "permissions": [] }
          ],
          "workflows": [
            { "module": "po", "statusField": "status", "initial": "draft",
              "states": [ {{states}} ],
              "transitions": [ {{transitions}} ] }
          ],
          "approvalFlows": [ {{flows}} ]
        }
        """;

    private static IReadOnlyList<string> Errors(string json)
    {
        var parsed = AppJson.Parse(json);
        parsed.Errors.Should().BeEmpty();
        return AppPackageValidator.Validate(parsed.V2!).Errors;
    }

    private static string With(string text, string from, string to)
    {
        text.Should().Contain(from);
        return text.Replace(from, to);
    }

    private static string Flows(params string[] flows) => string.Join(", ", flows);

    // ---- 結構層 ----

    [Fact]
    public void ValidFormat8Package_Passes_AndKeepsFlowsTransitionsAndEditable()
    {
        var json = Package(Flows(LargeFlow, MidFlow));
        Errors(json).Should().BeEmpty();
        var p = AppJson.Parse(json).V2!;
        p.ApprovalFlows.Select(f => f.Code).Should().Equal("t8-po-large", "t8-po-mid");
        p.ApprovalFlows[0].Stages!.Select(s => s.Code).Should().Equal("lead", "board");
        p.ApprovalFlows[0].FallbackApprovers!.Groups.Should().Equal("purchaseLead");
        var wf = p.Workflows.Single();
        wf.Transitions.Single(t => t.From == "draft" && t.To == "confirmed").Approval!.NoMatch.Should().Be("direct");
        wf.Transitions.Single(t => t.From == "draft" && t.To == "cancelled").Approval.Should().BeNull();
        wf.States.Single(s => s.Option == "cancelled").Editable.Should().BeTrue();
        wf.States.Single(s => s.Option == "draft").Editable.Should().BeNull();
    }

    [Fact]
    public void Format7_WithApprovalFlows_Rejected()
    {
        // 舊讀取器靜默略過簽核設定，客戶會以為單據有簽核卻沒有（§14.1），整份拒絕
        Errors(Package(LargeFlow, formatVersion: 7)).Should().Contain("approvalFlows: formatVersion 8 才支援 approvalFlows");
    }

    [Fact]
    public void Format7_WithTransitionApproval_Rejected()
    {
        Errors(Package("", formatVersion: 7, states: """{ "option": "draft" }, { "option": "confirmed" }, { "option": "cancelled" }"""))
            .Should().Contain("workflows[po].transitions[draft>confirmed].approval: formatVersion 8 才支援 approval");
    }

    [Fact]
    public void Format7_WithEditableState_Rejected()
    {
        var transitions = """{ "from": "draft", "to": "confirmed", "groups": [] }, { "from": "draft", "to": "cancelled", "groups": [] }""";
        Errors(Package("", formatVersion: 7, transitions: transitions))
            .Should().Contain("workflows[po].states[cancelled].editable: formatVersion 8 才支援 editable");
    }

    [Fact]
    public void Format9_Parse_NeedsNewerVersion()
    {
        AppJson.Parse(Package(LargeFlow, formatVersion: 9)).Errors.Should().ContainSingle()
            .Which.Should().Be("這個套件需要較新版本的 Formwright");
    }

    [Fact]
    public void MasterPackage_ApprovalFlows_Rejected()
    {
        Errors(Package(LargeFlow, kind: ", \"kind\": \"master\"")).Should().Contain("approvalFlows: 主檔套件不收 approvalFlows");
    }

    [Fact]
    public void Shape_MissingOrGraph_Rejected()
    {
        Errors(Package(With(LargeFlow, "\"shape\": \"sequence\",", ""))).Should().Contain("approvalFlows[t8-po-large].shape: 必填");
        Errors(Package(With(LargeFlow, "\"shape\": \"sequence\"", "\"shape\": \"graph\"")))
            .Should().Contain("approvalFlows[t8-po-large].shape: 格式 8 只支援 sequence");
    }

    [Fact]
    public void Stages_EmptyOrOverTechnicalMax_Rejected()
    {
        var max = PolicyCatalog.ApprovalMaxStages.Max;
        var message = $"approvalFlows[t8-po-mid].stages: {ApprovalMessages.StagesCount(max)}";
        var start = MidFlow.IndexOf("\"stages\": [", StringComparison.Ordinal);
        var empty = MidFlow[..start] + "\"stages\": [] }";
        Errors(Package(empty)).Should().Contain(message);

        var stages = string.Join(", ", Enumerable.Range(1, max + 1).Select(i =>
            $$"""{ "code": "s{{i}}", "name": { "zh-TW": "第{{i}}關" }, "approvers": { "source": "manager", "level": 1 }, "mode": "any" }"""));
        var tooMany = MidFlow[..start] + $"\"stages\": [ {stages} ] }}";
        Errors(Package(tooMany)).Should().Contain(message);
    }

    [Fact]
    public void UsersSource_Rejected()
    {
        Errors(Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", "\"source\": \"users\"")))
            .Should().Contain("approvalFlows[t8-po-mid].stages[lead].approvers: 套件不收指定使用者（帳號屬於這個資料庫）");
    }

    [Fact]
    public void UserIdsProperty_ParseRejected()
    {
        var json = Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", "\"source\": \"users\", \"userIds\": [\"buyer01\"]"));
        AppJson.Parse(json).Errors.Should().ContainSingle().Which.Should().StartWith("套件格式錯誤");
    }

    [Fact]
    public void FallbackSource_UserFieldOrPick_Rejected()
    {
        const string from = "\"fallbackApprovers\": { \"source\": \"group\", \"groups\": [\"purchaseLead\"] }";
        Errors(Package(With(LargeFlow, from, "\"fallbackApprovers\": { \"source\": \"userField\", \"field\": \"po.buyer\", \"candidateGroups\": [\"buyers\"] }")))
            .Should().Contain("approvalFlows[t8-po-large].fallbackApprovers: 備援簽核人只能是群組或主管");
        Errors(Package(With(LargeFlow, from, "\"fallbackApprovers\": { \"source\": \"initiatorPick\", \"min\": 1, \"max\": 2 }")))
            .Should().Contain("approvalFlows[t8-po-large].fallbackApprovers: 備援簽核人只能是群組或主管");
    }

    [Fact]
    public void FlowCode_FormatReservedDuplicate_Rejected()
    {
        Errors(Package(With(MidFlow, "\"code\": \"t8-po-mid\"", "\"code\": \"order\"")))
            .Should().Contain($"approvalFlows[0].code: {ApprovalMessages.FlowKeyReserved}");
        Errors(Package(With(MidFlow, "\"code\": \"t8-po-mid\"", "\"code\": \"A-1\"")))
            .Should().Contain("approvalFlows[0].code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
        Errors(Package(Flows(MidFlow, With(MidFlow, "\"priority\": 2", "\"priority\": 3"))))
            .Should().Contain("approvalFlows[1].code: 流程代碼重複 t8-po-mid");
    }

    [Fact]
    public void StageCode_FormatDuplicate_Rejected()
    {
        Errors(Package(With(MidFlow, "\"code\": \"lead\"", "\"code\": \"Lead_1\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].stages[Lead_1].code: {ApprovalMessages.StageCodeInvalid}");
        Errors(Package(With(LargeFlow, "\"code\": \"board\"", "\"code\": \"lead\"")))
            .Should().Contain($"approvalFlows[t8-po-large].stages[lead].code: {ApprovalMessages.StageCodeDuplicate}");
    }

    [Fact]
    public void Mode_OnEmpty_DueHours_Invalid_Rejected()
    {
        Errors(Package(With(MidFlow, "\"mode\": \"any\"", "\"mode\": \"some\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].stages[lead].mode: {ApprovalMessages.ModeInvalid}");
        Errors(Package(With(MidFlow, "\"onEmpty\": \"fallback\"", "\"onEmpty\": \"wait\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].stages[lead].onEmpty: {ApprovalMessages.OnEmptyInvalid}");
        var max = PolicyCatalog.ApprovalMaxDueHours.Max;
        Errors(Package(With(LargeFlow, "\"dueHours\": 24", $"\"dueHours\": {max + 1}")))
            .Should().Contain($"approvalFlows[t8-po-large].stages[lead].dueHours: {ApprovalMessages.DueHours(max)}");
        Errors(Package(With(LargeFlow, "\"dueHours\": 24", "\"dueHours\": 0")))
            .Should().Contain($"approvalFlows[t8-po-large].stages[lead].dueHours: {ApprovalMessages.DueHours(max)}");
    }

    [Fact]
    public void ManagerLevel_OutOfTechnicalRange_Rejected()
    {
        var max = PolicyCatalog.ApprovalMaxManagerLevel.Max;
        var message = $"approvalFlows[t8-po-mid].stages[lead].approvers: {ApprovalMessages.ManagerLevel(max)}";
        Errors(Package(With(MidFlow, "\"level\": 1", "\"level\": 0"))).Should().Contain(message);
        Errors(Package(With(MidFlow, "\"level\": 1", $"\"level\": {max + 1}"))).Should().Contain(message);
    }

    [Fact]
    public void Pick_MinMax_Rejected()
    {
        var max = PolicyCatalog.ApprovalMaxApprovers.Max;
        var message = $"approvalFlows[t8-po-mid].stages[lead].approvers: {ApprovalMessages.PickRange(max)}";
        string Pick(int min, int pickMax) =>
            Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", $"\"source\": \"initiatorPick\", \"min\": {min}, \"max\": {pickMax}"));
        Errors(Pick(0, 2)).Should().Contain(message);
        Errors(Pick(1, max + 1)).Should().Contain(message);
        Errors(Pick(3, 2)).Should().Contain(message);
        Errors(Pick(1, 2)).Should().BeEmpty();
    }

    [Fact]
    public void UserField_WithoutCandidateGroups_Rejected()
    {
        Errors(Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", "\"source\": \"userField\", \"field\": \"po.buyer\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].stages[lead].approvers: {ApprovalMessages.UserFieldWithoutCandidates}");
    }

    [Fact]
    public void GroupSource_WithoutGroups_Rejected()
    {
        Errors(Package(With(LargeFlow, "\"groups\": [\"board\", \"buyers\"]", "\"groups\": []")))
            .Should().Contain("approvalFlows[t8-po-large].stages[board].approvers: 群組至少要有一個");
    }

    [Fact]
    public void TooManyFlows_Rejected()
    {
        var flows = Enumerable.Range(1, AppPackageValidator.MaxApprovalFlows + 1)
            .Select(i => With(With(MidFlow, "\"code\": \"t8-po-mid\"", $"\"code\": \"t8-po-{i}\""), "\"priority\": 2", $"\"priority\": {i}"));
        Errors(Package(string.Join(", ", flows))).Should().Contain($"approvalFlows: 最多 {AppPackageValidator.MaxApprovalFlows} 個");
    }

    [Fact]
    public void TransitionApproval_RequiredFalse_Rejected()
    {
        Errors(Package(LargeFlow, transitions: With(Transitions, "\"required\": true", "\"required\": false")))
            .Should().Contain("workflows[po].transitions[draft>confirmed].approval.required: 必須是 true（不需簽核就省略 approval）");
    }

    [Fact]
    public void NoMatch_Unknown_Rejected()
    {
        Errors(Package(LargeFlow, transitions: With(Transitions, "\"noMatch\": \"direct\"", "\"noMatch\": \"skip\"")))
            .Should().Contain("workflows[po].transitions[draft>confirmed].approval.noMatch: 必須是 block 或 direct");
    }
}
