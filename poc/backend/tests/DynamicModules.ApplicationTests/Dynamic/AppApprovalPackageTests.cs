using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
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

    private static string Package(string flows, int formatVersion = 8, string transitions = Transitions, string states = States, string kind = "", string extraWorkflows = "") => $$"""
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
              "transitions": [ {{transitions}} ] }{{extraWorkflows}}
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
    // ---- 引用層 ----

    [Fact]
    public void Module_NotInPackageOrWithoutWorkflow_Rejected()
    {
        Errors(Package(With(MidFlow, "\"module\": \"po\"", "\"module\": \"nope\"")))
            .Should().Contain("approvalFlows[t8-po-mid].module: 模組 nope 不在套件內");
        Errors(Package(With(With(MidFlow, "\"module\": \"po\"", "\"module\": \"vendor\""), "\"to\": \"confirmed\"", "\"to\": \"ok\"")))
            .Should().Contain("approvalFlows[t8-po-mid].module: 簽核流程只能掛在有狀態機的獨立模組上");
    }

    [Fact]
    public void Module_IsDetail_Rejected()
    {
        // 審查 S3：明細模組也有狀態機時，拒收的唯一原因是「它是明細」
        const string lineWorkflow = """
            , { "module": "line", "statusField": "state", "initial": "open",
                "states": [ { "option": "open" }, { "option": "closed" } ],
                "transitions": [ { "from": "open", "to": "closed", "groups": [], "approval": { "required": true } } ] }
            """;
        var lineFlow = With(With(MidFlow, "\"module\": \"po\"", "\"module\": \"line\""), "\"to\": \"confirmed\"", "\"to\": \"closed\"");
        lineFlow = With(lineFlow, "\"code\": \"t8-po-mid\"", "\"code\": \"t8-line\"");
        lineFlow = With(lineFlow, "\"field\": \"po.total\", \"op\": \"gte\", \"value\": 10000", "\"field\": \"line.item\", \"op\": \"isNotEmpty\"");
        lineFlow = With(lineFlow, "\"editableFields\": [\"po.memo\"]", "\"editableFields\": []");
        var errors = Errors(Package(Flows(LargeFlow, MidFlow, lineFlow), extraWorkflows: lineWorkflow));
        errors.Should().Equal("approvalFlows[t8-line].module: 簽核流程只能掛在有狀態機的獨立模組上");
    }

    [Fact]
    public void To_NotAStateOrIsInitial_Rejected()
    {
        Errors(Package(With(MidFlow, "\"to\": \"confirmed\"", "\"to\": \"shipped\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].to: {ApprovalMessages.TargetStateInvalid}");
        Errors(Package(With(MidFlow, "\"to\": \"confirmed\"", "\"to\": \"draft\"")))
            .Should().Contain($"approvalFlows[t8-po-mid].to: {ApprovalMessages.TargetStateInvalid}");
    }

    [Fact]
    public void RejectTo_NotAState_Rejected()
    {
        Errors(Package(With(LargeFlow, "\"rejectTo\": \"draft\"", "\"rejectTo\": \"shipped\"")))
            .Should().Contain($"approvalFlows[t8-po-large].rejectTo: {ApprovalMessages.RejectToInvalidState}");
    }

    [Fact]
    public void Flow_TargetWithoutApprovalTransition_WarnsOnly()
    {
        // §14.4：流程的目標狀態沒有需簽核轉移進入時流程不會被用到；設計器允許這種狀態、往返要讀得回來，所以只警告（審查 S2，主控裁決）
        var cancelFlow = With(With(MidFlow, "\"code\": \"t8-po-mid\"", "\"code\": \"t8-po-cancel\""), "\"to\": \"confirmed\"", "\"to\": \"cancelled\"");
        var result = AppPackageValidator.Validate(AppJson.Parse(Package(Flows(LargeFlow, MidFlow, cancelFlow))).V2!);
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Contain("approvalFlows[t8-po-cancel].to: 狀態「已取消」沒有需簽核的轉移進入，這個流程不會被用到");
    }

    [Fact]
    public void InvalidFlow_DoesNotAlsoReportNoFlow()
    {
        // 審查 N3：唯一的流程因 to 錯誤被排除時，不再多報「已確認沒有流程」
        var errors = Errors(Package(With(MidFlow, "\"to\": \"confirmed\"", "\"to\": \"shipped\"")));
        errors.Should().Contain($"approvalFlows[t8-po-mid].to: {ApprovalMessages.TargetStateInvalid}");
        errors.Should().NotContain(e => e.Contains(ApprovalMessages.NoFlow("已確認")));
    }

    [Fact]
    public void InvalidFlow_SkipsNoFlowOnlyForItsTarget()
    {
        // 複審 N-a：流程因 rejectTo 錯誤被排除時，只略過它目標（已確認）的「沒有流程」；同模組另一個真的缺流程的狀態（已退回）照報
        var transitions = Transitions + """, { "from": "draft", "to": "returned", "groups": [], "approval": { "required": true } }""";
        var errors = Errors(Package(With(MidFlow, "\"priority\": 2,", "\"priority\": 2, \"rejectTo\": \"nowhere\","), transitions: transitions));
        errors.Should().Contain($"approvalFlows[t8-po-mid].rejectTo: {ApprovalMessages.RejectToInvalidState}");
        errors.Should().NotContain(e => e.Contains(ApprovalMessages.NoFlow("已確認")));
        errors.Should().Contain(e => e.Contains(ApprovalMessages.NoFlow("已退回")));
    }

    [Fact]
    public void DuplicatePriority_SameTarget_Rejected()
    {
        Errors(Package(Flows(LargeFlow, With(MidFlow, "\"priority\": 2", "\"priority\": 1"))))
            .Should().Contain("approvalFlows[t8-po-mid].priority: 同一目標狀態上的流程優先序不能重複");
    }

    [Fact]
    public void Condition_FieldOfAnotherModule_Rejected()
    {
        Errors(Package(With(MidFlow, "\"field\": \"po.total\"", "\"field\": \"vendor.rating\"")))
            .Should().Contain("approvalFlows[t8-po-mid].when: vendor.rating 必須是模組 po 的欄位");
    }

    [Fact]
    public void Condition_UnknownField_Rejected()
    {
        Errors(Package(With(MidFlow, "\"field\": \"po.total\"", "\"field\": \"po.nothing\"")))
            .Should().Contain("approvalFlows[t8-po-mid].when: 條件引用了套件沒有的欄位 po.nothing");
    }

    [Fact]
    public void Condition_UserConstant_Rejected()
    {
        var stageWhen = "\"approvers\": { \"source\": \"manager\", \"level\": 1 }, \"when\": { \"all\": [ { \"field\": \"po.buyer\", \"op\": \"eq\", \"value\": \"buyer01\" } ] },";
        Errors(Package(With(MidFlow, "\"approvers\": { \"source\": \"manager\", \"level\": 1 },", stageWhen)))
            .Should().Contain(e => e.StartsWith("approvalFlows[t8-po-mid].stages[lead].when: ") && e.Contains(AppEffectsCodec.ConcreteRecordMessage));
    }

    [Fact]
    public void UserField_NotUserType_Rejected()
    {
        Errors(Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", "\"source\": \"userField\", \"field\": \"po.memo\", \"candidateGroups\": [\"buyers\"]")))
            .Should().Contain("approvalFlows[t8-po-mid].stages[lead].approvers: po.memo 必須是模組 po 的使用者欄位");
    }

    [Fact]
    public void EditableField_NotInModule_Rejected()
    {
        Errors(Package(With(MidFlow, "\"editableFields\": [\"po.memo\"]", "\"editableFields\": [\"vendor.name\"]")))
            .Should().Contain("approvalFlows[t8-po-mid].stages[lead].editableFields: vendor.name 必須是模組 po 的欄位");
    }

    [Fact]
    public void Group_NotDeclared_Rejected()
    {
        Errors(Package(With(LargeFlow, "\"groups\": [\"board\", \"buyers\"]", "\"groups\": [\"board\", \"ghost\"]")))
            .Should().Contain("approvalFlows[t8-po-large].stages[board].approvers: 群組 ghost 沒有在 groups 宣告");
        Errors(Package(With(MidFlow, "\"source\": \"manager\", \"level\": 1", "\"source\": \"userField\", \"field\": \"po.buyer\", \"candidateGroups\": [\"ghost\"]")))
            .Should().Contain("approvalFlows[t8-po-mid].stages[lead].approvers: 群組 ghost 沒有在 groups 宣告");
        Errors(Package(With(LargeFlow, "\"groups\": [\"purchaseLead\"] },", "\"groups\": [\"ghost\"] },")))
            .Should().Contain("approvalFlows[t8-po-large].fallbackApprovers: 群組 ghost 沒有在 groups 宣告");
    }

    // ---- 狀態一致性（ApprovalConstraints） ----

    [Fact]
    public void ApprovalTarget_WithoutFlow_Rejected()
    {
        Errors(Package("")).Should().Contain($"workflows[po].states[confirmed]: {ApprovalMessages.NoFlow("已確認")}");
    }

    [Fact]
    public void EntryMismatch_PlainEntryIntoTarget_Rejected()
    {
        var transitions = Transitions + """, { "from": "returned", "to": "confirmed", "groups": [] }""";
        Errors(Package(Flows(LargeFlow, MidFlow), transitions: transitions))
            .Should().Contain($"workflows[po].transitions[returned>confirmed]: {ApprovalMessages.EntryMismatch("已確認")}");
    }

    [Fact]
    public void DirectEntry_AllFlowsInactive_Rejected()
    {
        Errors(Package(Flows(With(LargeFlow, "\"active\": true", "\"active\": false"), With(MidFlow, "\"active\": true", "\"active\": false"))))
            .Should().Contain($"workflows[po].states[confirmed]: {ApprovalMessages.DirectNeedsActive("已確認")}");
    }

    [Fact]
    public void PlainTransitionIntoLockedState_Rejected()
    {
        // 已確認 → 已退回 不需簽核、已退回不是可編輯狀態：鎖定延伸到已退回；草稿 → 已退回 不簽就進入鎖定
        var transitions = Transitions + """, { "from": "confirmed", "to": "returned", "groups": [] }, { "from": "draft", "to": "returned", "groups": [] }""";
        Errors(Package(Flows(LargeFlow, MidFlow), transitions: transitions))
            .Should().Contain($"workflows[po].transitions[draft>returned]: {ApprovalMessages.LockEntry("草稿", "已退回")}");
    }

    [Fact]
    public void InitialStateLocked_Rejected()
    {
        // 起始狀態不能當流程目標（to 檢查），但轉移仍可能把它標成需簽核：鎖定一致性要擋
        var transitions = With(Transitions, "{ \"from\": \"cancelled\", \"to\": \"draft\", \"groups\": [] }",
            "{ \"from\": \"cancelled\", \"to\": \"draft\", \"groups\": [], \"approval\": { \"required\": true } }");
        Errors(Package(Flows(LargeFlow, MidFlow), transitions: transitions))
            .Should().Contain($"workflows[po].initial: {ApprovalMessages.InitialLocked}");
    }

    [Fact]
    public void RejectTo_UndefinedOrNeedsApproval_Rejected()
    {
        var name = "採購單（大額）";
        Errors(Package(Flows(With(LargeFlow, "\"rejectTo\": \"draft\"", "\"rejectTo\": \"returned\""), MidFlow)))
            .Should().Contain($"workflows[po].flows[t8-po-large]: {ApprovalMessages.RejectToInvalid(name, "草稿", "已退回")}");
    }

    [Fact]
    public void RejectTo_LockedFromUnlocked_Rejected()
    {
        // 已確認 → 已退回 不需簽核：已退回被鎖定延伸；草稿 → 已退回 也定義為需簽核（不違反進入鎖定），但退回到鎖定狀態不行
        var transitions = Transitions + """, { "from": "confirmed", "to": "returned", "groups": [] }, { "from": "draft", "to": "returned", "groups": [], "approval": { "required": true } }""";
        var returnedFlow = With(With(MidFlow, "\"code\": \"t8-po-mid\"", "\"code\": \"t8-po-ret\""), "\"to\": \"confirmed\"", "\"to\": \"returned\"");
        Errors(Package(Flows(With(LargeFlow, "\"rejectTo\": \"draft\"", "\"rejectTo\": \"confirmed\""), MidFlow, returnedFlow), transitions: transitions))
            .Should().Contain($"workflows[po].flows[t8-po-large]: {ApprovalMessages.RejectToLocked("採購單（大額）")}");
    }

    [Fact]
    public void EditableFields_FromReadOnlyState_Rejected()
    {
        var states = States.Replace("{ \"option\": \"draft\" }", "{ \"option\": \"draft\", \"readOnly\": true }");
        Errors(Package(Flows(LargeFlow, MidFlow), states: states))
            .Should().Contain($"workflows[po].flows[t8-po-mid]: {ApprovalMessages.EditableFromLocked("採購單（一萬以上）")}");
    }

    [Fact]
    public void EditableStateIsApprovalTarget_Rejected()
    {
        var states = States.Replace("{ \"option\": \"confirmed\", \"readOnly\": true }", "{ \"option\": \"confirmed\", \"editable\": true }");
        Errors(Package(Flows(LargeFlow, MidFlow), states: states))
            .Should().Contain($"workflows[po].states[confirmed]: {ApprovalMessages.LockedMarkedEditable("已確認")}");
    }

    [Fact]
    public void ErrorsUseCodes_NotTemporaryIds()
    {
        var transitions = Transitions + """, { "from": "returned", "to": "confirmed", "groups": [] }, { "from": "confirmed", "to": "returned", "groups": [] }, { "from": "draft", "to": "returned", "groups": [] }""";
        var errors = Errors(Package(Flows(With(LargeFlow, "\"rejectTo\": \"draft\"", "\"rejectTo\": \"returned\""), MidFlow), transitions: transitions));
        errors.Where(e => e.StartsWith("workflows[po].")).Should().HaveCountGreaterThan(2);
        errors.Should().NotContain(e => System.Text.RegularExpressions.Regex.IsMatch(e, @"\[\d+(>|\])|>\d+\]"));
        errors.Should().Contain(e => e.Contains("「已確認」"));
    }

    [Fact]
    public void ApprovalLimitErrors_UsesGivenLimits()
    {
        var p = AppJson.Parse(Package(Flows(LargeFlow, MidFlow))).V2!;
        AppPackageValidator.ApprovalLimitErrors(p, new ApprovalLimitSet(1, 20, 8760, 5))
            .Should().Equal($"approvalFlows[t8-po-large].stages: {ApprovalMessages.StagesCount(1)}");
        AppPackageValidator.ApprovalLimitErrors(p, new ApprovalLimitSet(20, 20, 8760, 5)).Should().BeEmpty();
        AppPackageValidator.ApprovalLimitErrors(p, new ApprovalLimitSet(20, 20, 24, 5))
            .Should().Equal($"approvalFlows[t8-po-large].stages[board].dueHours: {ApprovalMessages.DueHours(24)}");
    }
}
