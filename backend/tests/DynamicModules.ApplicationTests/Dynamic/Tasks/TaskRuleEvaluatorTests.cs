using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務規則的純函式：觸發的邊緣判斷、到期日（台北營業日）、負責人（設計 §5）。</summary>
public class TaskRuleEvaluatorTests
{
    private const long StartField = 11, EndField = 12, StageField = 13, FlagField = 14, UserField = 15, TextField = 16;

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Module BuildModule()
    {
        Field F(long id, FieldType type) => new() { Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active };
        return new Module
        {
            Blocks = [new Block { Fields = [F(StartField, FieldType.Date), F(EndField, FieldType.DateTime), F(StageField, FieldType.Enum),
                F(FlagField, FieldType.Boolean), F(UserField, FieldType.User), F(TextField, FieldType.Text)] }]
        };
    }

    private static Dictionary<string, JsonElement?> Values(params (long Field, string Json)[] items) =>
        items.ToDictionary(i => $"f_{i.Field}", i => (JsonElement?)J(i.Json));

    private static TaskRuleTask Def(long? due, int offset = 0, long assignee = UserField) =>
        new() { Code = "t", DueFieldId = due, DueOffsetDays = offset, AssigneeFieldId = assignee };

    [Theory]
    [InlineData(-62, "2026-12-01")]
    [InlineData(-1, "2027-01-31")]
    [InlineData(0, "2027-02-01")]
    public void DueDate_DateField_Offsets(int offset, string expected)
    {
        var due = TaskRuleEvaluator.DueDate(Def(StartField, offset), BuildModule(), Values((StartField, "\"2027-02-01\"")));
        due.Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void DueDate_EndField_Plus12()
    {
        var due = TaskRuleEvaluator.DueDate(Def(EndField, 12), BuildModule(), Values((EndField, "\"2027-02-03T09:00:00+08:00\"")));
        due.Should().Be(new DateOnly(2027, 2, 15));
    }

    [Theory]
    [InlineData("2027-01-31T17:00:00Z", 0, "2027-02-01")]
    [InlineData("2027-02-01T00:30:00+08:00", 0, "2027-02-01")]
    [InlineData("2027-01-31T15:59:59Z", 0, "2027-01-31")]
    [InlineData("2027-01-31T17:00:00Z", 1, "2027-02-02")]
    [InlineData("2027-02-01T23:00:00", 0, "2027-02-01")]
    public void DueDate_DateTime_UsesTaipeiDate(string value, int offset, string expected)
    {
        var due = TaskRuleEvaluator.DueDate(Def(EndField, offset), BuildModule(), Values((EndField, $"\"{value}\"")));
        due.Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void DueDate_EmptyBase_Null()
    {
        var module = BuildModule();
        TaskRuleEvaluator.DueDate(Def(null), module, Values((StartField, "\"2027-02-01\""))).Should().BeNull("沒有到期基準欄位");
        TaskRuleEvaluator.DueDate(Def(StartField), module, Values()).Should().BeNull("記錄沒有值");
        TaskRuleEvaluator.DueDate(Def(StartField), module, Values((StartField, "null"))).Should().BeNull();
        TaskRuleEvaluator.DueDate(Def(999), module, Values((StartField, "\"2027-02-01\""))).Should().BeNull("欄位不在模組裡");
        TaskRuleEvaluator.DueDate(Def(StartField), module, Values((StartField, "\"not a date\""))).Should().BeNull();
        TaskRuleEvaluator.DueDate(Def(StartField, 3650), module, Values((StartField, "\"9999-12-30\""))).Should().BeNull("超出日期範圍不丟例外");
    }

    [Fact]
    public void Assignee_InactiveOrUnknown_Null()
    {
        var active = new HashSet<string> { "sales01" };
        TaskRuleEvaluator.Assignee(Def(null), Values((UserField, "\"sales01\"")), active).Should().Be("sales01");
        TaskRuleEvaluator.Assignee(Def(null), Values((UserField, "\"gone\"")), active).Should().BeNull("不存在或已停用");
        TaskRuleEvaluator.Assignee(Def(null), Values((UserField, "\"\"")), active).Should().BeNull();
        TaskRuleEvaluator.Assignee(Def(null), Values((UserField, "null")), active).Should().BeNull();
        TaskRuleEvaluator.Assignee(Def(null), Values(), active).Should().BeNull();
    }

    [Fact]
    public void Changed_ComparesRawText()
    {
        var before = Values((TextField, "\"a\""), (StartField, "\"2027-02-01\""));
        var after = Values((TextField, "\"b\""), (StartField, "\"2027-02-01\""));
        TaskRuleEvaluator.Changed(TextField, before, after).Should().BeTrue();
        TaskRuleEvaluator.Changed(StartField, before, after).Should().BeFalse();
        TaskRuleEvaluator.Changed(null, before, after).Should().BeFalse("沒有指定欄位");
        TaskRuleEvaluator.Changed(TextField, null, after).Should().BeTrue("沒有舊值，新值有內容就算變動");
        TaskRuleEvaluator.Changed(FlagField, before, after).Should().BeFalse("兩邊都沒有值");
        TaskRuleEvaluator.Changed(TextField, Values((TextField, "null")), Values()).Should().BeFalse("null 與沒有一樣");
    }

    private static TaskRuleTrigger T(string on, long? field = null, string? valueJson = null) =>
        new(on, field, valueJson is null ? null : J(valueJson));

    [Fact]
    public void Triggers_OnCreate_OnlyWhenAdded()
    {
        var v = Values((TextField, "\"x\""));
        TaskRuleEvaluator.Triggers(T("onCreate"), added: true, null, true, null, v).Should().BeTrue();
        TaskRuleEvaluator.Triggers(T("onCreate"), added: false, true, true, v, v).Should().BeFalse();
    }

    [Fact]
    public void Triggers_Manual_NeverFiresFromJournal()
    {
        var v = Values();
        TaskRuleEvaluator.Triggers(T("manual"), added: true, null, true, null, v).Should().BeFalse();
        TaskRuleEvaluator.Triggers(T("manual"), added: false, false, true, v, v).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null, true, true)]      // 新增且符合：觸發
    [InlineData(true, null, false, false)]    // 新增但不符合
    [InlineData(false, false, true, true)]    // 修改：由不符合變成符合
    [InlineData(false, true, true, false)]    // 修改：本來就符合，不再觸發
    [InlineData(false, true, false, false)]   // 修改：變成不符合
    [InlineData(false, false, false, false)]  // 修改：一直不符合
    [InlineData(false, null, true, true)]     // 修改但不知道舊狀態：視為本來不符合
    public void Triggers_OnUpdateMatch_IsEdgeTriggered(bool added, bool? before, bool after, bool expected)
    {
        var v = Values();
        TaskRuleEvaluator.Triggers(T("onUpdateMatch"), added, before, after, added ? null : v, v).Should().Be(expected);
    }

    [Fact]
    public void Triggers_OnFieldBecomes_FiresAtTheMomentOfBecoming()
    {
        var t = T("onFieldBecomes", StageField, "\"opt_5\"");
        var other = Values((StageField, "\"opt_4\""));
        var target = Values((StageField, "\"opt_5\""));

        TaskRuleEvaluator.Triggers(t, false, null, true, other, target).Should().BeTrue("變成指定值的那一刻");
        TaskRuleEvaluator.Triggers(t, false, null, true, target, target).Should().BeFalse("本來就是指定值");
        TaskRuleEvaluator.Triggers(t, false, null, true, target, other).Should().BeFalse("離開指定值");
        TaskRuleEvaluator.Triggers(t, true, null, true, null, target).Should().BeTrue("新增時就是指定值也算");
        TaskRuleEvaluator.Triggers(t, true, null, true, null, other).Should().BeFalse();
        TaskRuleEvaluator.Triggers(t, false, null, true, Values(), target).Should().BeTrue("舊值是空的");
    }

    [Fact]
    public void Triggers_OnFieldBecomes_Boolean_And_MissingValue()
    {
        var t = T("onFieldBecomes", FlagField, "true");
        TaskRuleEvaluator.Triggers(t, false, null, true, Values((FlagField, "false")), Values((FlagField, "true"))).Should().BeTrue();
        TaskRuleEvaluator.Triggers(t, false, null, true, Values((FlagField, "true")), Values((FlagField, "true"))).Should().BeFalse();

        TaskRuleEvaluator.Triggers(new("onFieldBecomes", FlagField, null), true, null, true, null, Values()).Should().BeFalse("沒設定指定值");
        TaskRuleEvaluator.Triggers(new("onFieldBecomes", null, J("true")), true, null, true, null, Values()).Should().BeFalse("沒設定欄位");
    }

    [Fact]
    public void Triggers_UnknownKind_False()
    {
        TaskRuleEvaluator.Triggers(T("bogus"), true, null, true, null, Values()).Should().BeFalse();
    }
}
