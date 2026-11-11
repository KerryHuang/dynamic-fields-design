using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>任務規則在資料庫（Id）與套件（代號）之間互轉（任務管理設計 §5.8）。</summary>
public class AppTaskRuleCodecTests
{
    private const long Owner = 11, Due = 12, Stage = 13, Hot = 101, Cold = 102;

    private static readonly Dictionary<long, string> FieldNames = new() { [Owner] = "lead.owner", [Due] = "lead.due", [Stage] = "lead.stage" };
    private static readonly Dictionary<long, string> OptionCodes = new() { [Hot] = "hot", [Cold] = "cold" };

    private static readonly AppReportCodec.Names Names = new(
        key => key == "t7-lead" ? "lead" : null,
        id => FieldNames.GetValueOrDefault(id),
        (field, option) => field == Stage ? OptionCodes.GetValueOrDefault(option) : null,
        id => id == Stage,
        id => id == Owner ? FieldType.User : null);

    private static readonly AppReportCodec.Lookups Lookups = new(
        code => code == "lead" ? "t7-lead" : null,
        name => FieldNames.FirstOrDefault(x => x.Value == name) is { Value: not null } hit ? hit.Key : null,
        name => name switch { "lead.hot" => Hot, "lead.cold" => Cold, _ => null },
        name => name switch { "lead.owner" => FieldType.User, "lead.due" => FieldType.Date, "lead.stage" => FieldType.Enum, _ => null });

    private static I18nText Text(string zh, string? en = null) =>
        new(en is null ? [KeyValuePair.Create("zh-TW", zh)] : [KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en)]);

    private static bool Same(JsonElement a, JsonElement b) => JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TaskRule Rule(TaskRuleRepeat repeat = TaskRuleRepeat.Once) => new()
    {
        Key = "t7-lead-follow", NameI18n = Text("跟進"), Active = true, Repeat = repeat,
        Trigger = new TaskRuleTrigger("onFieldBecomes", Stage, Json($"\"opt_{Hot}\"")),
        WhenJson = $$"""{"all":[{"field":{{Stage}},"op":"ne","value":"opt_{{Cold}}"}]}""",
        Tasks =
        [
            new TaskRuleTask { Code = "visit", SortOrder = 1, TitleI18n = Text("拜訪", "Visit"), AssigneeFieldId = Owner },
            new TaskRuleTask
            {
                Code = "call", SortOrder = 0, TitleI18n = Text("打電話", "Call"), DescriptionI18n = Text("先打電話"),
                AssigneeFieldId = Owner, DueFieldId = Due, DueOffsetDays = -3,
                WhenJson = $$"""{"all":[{"field":{{Due}},"op":"isNotEmpty"}]}"""
            }
        ]
    };

    [Fact]
    public void RoundTrip_PreservesTriggerTasksAndConditions()
    {
        var (package, errors) = AppTaskRuleCodec.ToPackage(Rule(), "lead", Names);
        errors.Should().BeEmpty();
        package!.Module.Should().Be("lead");
        package.Trigger.Field.Should().Be("lead.stage");
        package.Tasks.Select(t => t.Code).Should().Equal("call", "visit");
        package.Tasks[0].Assignee.Should().Be("lead.owner");
        package.Tasks[0].Due.Should().Be(new AppTaskDue("lead.due", -3));
        package.Tasks[1].Due.Should().BeNull();
        package.When!.Value.GetRawText().Should().Contain("\"lead.stage\"").And.Contain("opt_cold");

        var (input, back) = AppTaskRuleCodec.ToInput(package, Lookups);
        back.Should().BeEmpty();
        input!.Key.Should().Be("t7-lead-follow");
        input.Trigger.On.Should().Be("onFieldBecomes");
        input.Trigger.FieldId.Should().Be(Stage);
        input.Trigger.Value!.Value.GetString().Should().Be($"opt_{Hot}");
        Same(input.When!.Value, Json(Rule().WhenJson!)).Should().BeTrue(input.When.Value.GetRawText());
        input.Tasks.Select(t => (t.Code, t.AssigneeFieldId, t.DueFieldId, t.DueOffsetDays)).Should()
            .Equal(("call", Owner, (long?)Due, -3), ("visit", Owner, (long?)null, 0));
        Same(input.Tasks[0].When!.Value, Json(Rule().Tasks[1].WhenJson!)).Should().BeTrue();
        input.Tasks[1].When.Should().BeNull();
    }

    [Fact]
    public void OptionValueTrigger_UsesOptionCode()
    {
        var (package, _) = AppTaskRuleCodec.ToPackage(Rule(), "lead", Names);
        package!.Trigger.Value!.Value.GetString().Should().Be("opt_hot", "選項 Id 換資料庫就不同，套件用選項代號");

        var missing = package with { Trigger = package.Trigger with { Value = Json("\"opt_warm\"") } };
        AppTaskRuleCodec.ToInput(missing, Lookups).Errors.Should().Contain(e => e.Contains("warm"));

        var unknownOption = Rule();
        unknownOption.Trigger = unknownOption.Trigger with { Value = Json("\"opt_999\"") };
        AppTaskRuleCodec.ToPackage(unknownOption, "lead", Names).Rule.Should().BeNull();
    }

    [Fact]
    public void BooleanTrigger_KeptAsIs()
    {
        var rule = Rule();
        FieldNames[14] = "lead.vip";
        try
        {
            rule.Trigger = new TaskRuleTrigger("onFieldBecomes", 14, Json("true"));
            var (package, errors) = AppTaskRuleCodec.ToPackage(rule, "lead", Names);
            errors.Should().BeEmpty();
            package!.Trigger.Value!.Value.ValueKind.Should().Be(JsonValueKind.True);
            AppTaskRuleCodec.ToInput(package, Lookups).Input!.Trigger.Value!.Value.ValueKind.Should().Be(JsonValueKind.True);
        }
        finally { FieldNames.Remove(14); }
    }

    [Fact]
    public void RoundTrip_PreservesRepeatAndI18nTitles()
    {
        var (once, _) = AppTaskRuleCodec.ToPackage(Rule(TaskRuleRepeat.Once), "lead", Names);
        once!.Repeat.Should().BeNull("once 是預設值，匯出省略這個鍵");
        AppTaskRuleCodec.ToInput(once, Lookups).Input!.Repeat.Should().Be(TaskRuleRepeat.Once);

        var (every, _) = AppTaskRuleCodec.ToPackage(Rule(TaskRuleRepeat.EveryTime), "lead", Names);
        every!.Repeat.Should().Be("everyTime");
        var input = AppTaskRuleCodec.ToInput(every, Lookups).Input!;
        input.Repeat.Should().Be(TaskRuleRepeat.EveryTime);
        input.Tasks[0].Title.Values.Should().Equal(Text("打電話", "Call").Values);
        input.Tasks[0].Description!.Resolve("zh-TW").Should().Be("先打電話");
        input.Name.Resolve("zh-TW").Should().Be("跟進");
    }

    [Fact]
    public void FieldOutsideThePackage_NotExported()
    {
        var rule = Rule();
        rule.Tasks[0].AssigneeFieldId = 999;
        var (package, errors) = AppTaskRuleCodec.ToPackage(rule, "lead", Names);
        package.Should().BeNull();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ExcludedField_OnImport_ReportsIt()
    {
        var (package, _) = AppTaskRuleCodec.ToPackage(Rule(), "lead", Names);
        var lookups = Lookups with { FieldId = name => name == "lead.due" ? null : Lookups.FieldId(name) };
        var (input, errors) = AppTaskRuleCodec.ToInput(package!, lookups);
        input.Should().BeNull();
        errors.Should().Contain(e => e.Contains("lead.due"));
    }
}
