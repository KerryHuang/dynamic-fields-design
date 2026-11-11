using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>套件格式 7 的 taskRules[]：結構層與引用層（任務管理設計 §5.8）。純函式，不需要資料庫。</summary>
public class AppTaskRulePackageTests
{
    private const string Rule = """
        { "code": "t7-lead-follow", "name": { "zh-TW": "跟進" }, "active": true, "module": "lead",
          "trigger": { "on": "onFieldBecomes", "field": "lead.stage", "value": "opt_hot" },
          "tasks": [ { "code": "call", "title": { "zh-TW": "打電話" }, "assignee": "lead.owner",
                       "due": { "field": "lead.due", "offsetDays": -1 } } ] }
        """;

    private const string MasterRule = """
        { "code": "t7-partner-visit", "name": { "zh-TW": "拜訪" }, "active": true, "module": "bp/partner",
          "trigger": { "on": "manual" },
          "tasks": [ { "code": "visit", "title": { "zh-TW": "拜訪客戶" }, "assignee": "bp/partner.accountMgr" } ] }
        """;

    private static string Package(string rules, int formatVersion = 7) => $$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "t7", "version": "1.0.0", "name": { "zh-TW": "任務規則測試" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [],
          "modules": [
            { "code": "lead", "key": "t7-lead", "name": { "zh-TW": "潛在客戶" }, "kind": "standalone", "titleField": "company",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "company", "type": "text", "label": { "zh-TW": "公司" }, "config": { "required": true } },
                { "code": "owner", "type": "user", "label": { "zh-TW": "負責業務" }, "config": {} },
                { "code": "due", "type": "date", "label": { "zh-TW": "預計成交" }, "config": {} },
                { "code": "stage", "type": "enum", "label": { "zh-TW": "階段" }, "config": {},
                  "options": [ { "code": "hot", "label": { "zh-TW": "熱" } }, { "code": "cold", "label": { "zh-TW": "冷" } } ] },
                { "code": "priority", "type": "enum", "label": { "zh-TW": "優先" }, "config": {},
                  "options": [ { "code": "urgent", "label": { "zh-TW": "急" } } ] }
              ] } ],
              "permissions": [] }
          ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "t7", "name": { "zh-TW": "任務" } },
            "fields": [ { "code": "accountMgr", "type": "user", "label": { "zh-TW": "客戶經理" }, "config": {} } ],
            "permissions": []
          } ],
          "taskRules": [ {{rules}} ]
        }
        """;

    private static IReadOnlyList<string> Errors(string json)
    {
        var parsed = AppJson.Parse(json);
        parsed.Errors.Should().BeEmpty();
        return AppPackageValidator.Validate(parsed.V2!).Errors;
    }

    private static string With(string rule, string from, string to)
    {
        rule.Should().Contain(from);
        return rule.Replace(from, to);
    }

    [Fact]
    public void ValidPackage_Passes_AndKeepsTheRules()
    {
        var json = Package($"{Rule}, {MasterRule}");
        Errors(json).Should().BeEmpty();
        var p = AppJson.Parse(json).V2!;
        p.TaskRules.Select(r => r.Code).Should().Equal("t7-lead-follow", "t7-partner-visit");
        p.TaskRules[0].Tasks.Single().Due!.OffsetDays.Should().Be(-1);
    }

    [Fact]
    public void Format6_WithTaskRules_Rejected()
    {
        // 格式 6 的讀取器靜默略過 taskRules 會讓客戶以為有自動派工，整份拒絕
        Errors(Package(Rule, formatVersion: 6)).Should().Contain("taskRules: formatVersion 7 才支援 taskRules");
    }

    [Fact]
    public void MasterPackage_TaskRules_Rejected()
    {
        var json = $$"""
            {
              "format": "formwright-app", "formatVersion": 7,
              "manifest": { "id": "bp", "kind": "master", "version": "1.0.0", "name": { "zh-TW": "往來對象" } },
              "groups": [],
              "modules": [ {
                "code": "partner", "key": "master-bp-partner", "name": { "zh-TW": "往來對象" }, "kind": "standalone",
                "titleField": "name", "extensible": true,
                "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                  { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                  { "code": "owner", "type": "user", "label": { "zh-TW": "負責人" }, "config": {} }
                ] } ],
                "permissions": []
              } ],
              "taskRules": [ { "code": "bp-visit", "name": { "zh-TW": "拜訪" }, "active": true, "module": "partner",
                "trigger": { "on": "manual" },
                "tasks": [ { "code": "visit", "title": { "zh-TW": "拜訪" }, "assignee": "partner.owner" } ] } ]
            }
            """;
        Errors(json).Should().Contain("taskRules: 主檔套件不收 taskRules");
    }

    [Fact]
    public void UnknownField_Rejected()
    {
        Errors(Package(With(Rule, "\"assignee\": \"lead.owner\"", "\"assignee\": \"lead.nobody\"")))
            .Should().Contain("taskRules[t7-lead-follow]: 引用了套件沒有的欄位 lead.nobody");
    }

    [Fact]
    public void AssigneeNotUser_Rejected()
    {
        Errors(Package(With(Rule, "\"assignee\": \"lead.owner\"", "\"assignee\": \"lead.company\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].tasks[call].assignee:") && e.Contains("負責人必須是這個模組的人員欄位"));
    }

    [Fact]
    public void DueNotDate_Rejected()
    {
        Errors(Package(With(Rule, "\"field\": \"lead.due\"", "\"field\": \"lead.company\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].tasks[call].due:") && e.Contains("到期基準必須是這個模組的日期欄位"));
    }

    [Fact]
    public void FieldOfAnotherModule_Rejected()
    {
        Errors(Package(With(MasterRule, "\"assignee\": \"bp/partner.accountMgr\"", "\"assignee\": \"lead.owner\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-partner-visit]") && e.Contains("lead.owner") && e.Contains("bp/partner"));
    }

    [Fact]
    public void TriggerOption_MustExist()
    {
        Errors(Package(With(Rule, "\"value\": \"opt_hot\"", "\"value\": \"opt_warm\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow]") && e.Contains("warm"));
    }

    [Fact]
    public void TriggerOption_FromOtherField_Rejected()
    {
        // lead.urgent 是 lead.priority 的選項，不屬於 lead.stage
        Errors(Package(With(Rule, "\"value\": \"opt_hot\"", "\"value\": \"opt_urgent\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].trigger.value:"));
        Errors(Package(With(Rule, "\"lead.stage\", \"value\": \"opt_hot\"", "\"lead.priority\", \"value\": \"opt_urgent\"")))
            .Should().BeEmpty();
    }

    [Fact]
    public void MasterBaseBooleanTrigger_Accepted()
    {
        // 主檔基本欄位的型別在套件驗證時查不到，true／false／opt_ 都放行，開啟時由 CreateTaskRuleRequest 用真實欄位判斷
        foreach (var value in new[] { "true", "false", "\"opt_vip\"" })
            Errors(Package(With(MasterRule, "{ \"on\": \"manual\" }", $$"""{ "on": "onFieldBecomes", "field": "bp/partner.vip", "value": {{value}} }""")))
                .Should().BeEmpty(value);
    }

    [Fact]
    public void TooManyTaskRules_Rejected()
    {
        var rules = string.Join(", ", Enumerable.Range(0, 51).Select(i => With(Rule, "t7-lead-follow", $"t7-rule-{i:00}")));
        Errors(Package(rules)).Should().Contain("taskRules: 最多 50 個");
    }

    [Fact]
    public void MasterSymbolModule_Accepted()
    {
        Errors(Package(MasterRule)).Should().BeEmpty();
    }

    [Fact]
    public void MasterSymbolModule_NotInRequires_Rejected()
    {
        Errors(Package(With(MasterRule, "\"module\": \"bp/partner\"", "\"module\": \"ct/contact\"")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-partner-visit]") && e.Contains("requires.masters"));
    }

    [Fact]
    public void ConditionWithUserConstant_Rejected()
    {
        var rule = With(Rule, "\"tasks\":", """ "when": { "all": [ { "field": "lead.owner", "op": "eq", "value": "alice" } ] }, "tasks":""");
        Errors(Package(rule)).Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow]") && e.Contains(AppEffectsCodec.ConcreteRecordMessage));
    }

    [Fact]
    public void Repeat_Invalid_Rejected()
    {
        var rule = With(Rule, "\"active\": true,", "\"active\": true, \"repeat\": \"twice\",");
        Errors(Package(rule)).Should().Contain("taskRules[t7-lead-follow].repeat: 必須是 once 或 everyTime");
    }

    [Fact]
    public void Repeat_EveryTime_Accepted()
    {
        var rule = With(Rule, "\"active\": true,", "\"active\": true, \"repeat\": \"everyTime\",");
        Errors(Package(rule)).Should().BeEmpty();
    }

    [Fact]
    public void Repeat_EveryTimeOnCreate_Rejected()
    {
        var rule = With(Rule, "\"active\": true,", "\"active\": true, \"repeat\": \"everyTime\",")
            .Replace("""{ "on": "onFieldBecomes", "field": "lead.stage", "value": "opt_hot" }""", """{ "on": "onCreate" }""");
        Errors(Package(rule)).Should().Contain("taskRules[t7-lead-follow].repeat: onCreate 只能 once");
    }

    [Fact]
    public void TaskTitle_UsesCheckText()
    {
        Errors(Package(With(Rule, "\"title\": { \"zh-TW\": \"打電話\" }", "\"title\": { \"en-US\": \"Call\" }")))
            .Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].tasks[call].title:"));
        Errors(Package(With(Rule, "\"title\": { \"zh-TW\": \"打電話\" }", $"\"title\": {{ \"zh-TW\": \"{new string('長', 201)}\" }}")))
            .Should().Contain("taskRules[t7-lead-follow].tasks[call].title: zh-TW 必須是 1 到 200 字");
    }

    [Fact]
    public void DuplicateCodes_Rejected()
    {
        Errors(Package($"{Rule}, {Rule}")).Should().Contain(e => e.Contains("taskRules[1].code") && e.Contains("t7-lead-follow"));
        Errors(Package(With(Rule, "\"tasks\": [ {", """ "tasks": [ { "code": "call", "title": { "zh-TW": "再打" }, "assignee": "lead.owner" }, {"""
            .TrimStart()))).Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].tasks") && e.Contains("重複"));
    }

    [Fact]
    public void UpdateMatch_RequiresCondition()
    {
        var rule = Rule.Replace("""{ "on": "onFieldBecomes", "field": "lead.stage", "value": "opt_hot" }""", """{ "on": "onUpdateMatch" }""");
        Errors(Package(rule)).Should().Contain(e => e.StartsWith("taskRules[t7-lead-follow].when:"));
    }
}
