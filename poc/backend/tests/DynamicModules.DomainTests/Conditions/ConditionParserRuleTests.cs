using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.Conditions;

public class ConditionParserRuleTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();

    private static Field Fld(long id, FieldType type, bool filterable = true) =>
        new() { Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active, Config = new FieldConfig { IsFilterable = filterable } };

    // 1 交期(date)、2 訂單日期(date)、3 數量(integer)、4 備註(textarea)、5 名稱(text, 不可篩選)
    private static Module Mod() => new()
    {
        Blocks = [new Block { Fields = [Fld(1, FieldType.Date), Fld(2, FieldType.Date), Fld(3, FieldType.Integer),
            Fld(4, FieldType.Textarea, false), Fld(5, FieldType.Text, false)] }]
    };

    private static ConditionParseResult P(string json, ConditionUsage usage = ConditionUsage.Rule) =>
        ConditionParser.Parse(J(json), Mod(), Registry, usage, new HashSet<string>());

    [Fact]
    public void Rule_FieldRef_Parsed()
    {
        var r = P("{\"field\":1,\"op\":\"gte\",\"value\":{\"fieldRef\":2}}");
        r.Error.Should().BeNull();
        var c = r.Node.Should().BeOfType<FieldCondition>().Subject;
        c.Field.Id.Should().Be(1);
        c.RefField!.Id.Should().Be(2);
        c.Op.Should().Be(FilterOperator.Gte);
        c.Values.Should().BeEmpty();
    }

    [Fact]
    public void Rule_FieldRef_TypeMismatch_Error() =>
        P("{\"field\":1,\"op\":\"gte\",\"value\":{\"fieldRef\":3}}").Error.Should().Be("欄位 1 的 fieldRef 型別不符");

    [Fact]
    public void Rule_FieldRef_BadOperator_Error() =>
        P("{\"field\":5,\"op\":\"contains\",\"value\":{\"fieldRef\":5}}").Error.Should().Be("欄位 5 不支援運算子 contains");

    [Fact]
    public void Rule_FieldRef_UnknownRef_Error() =>
        P("{\"field\":1,\"op\":\"eq\",\"value\":{\"fieldRef\":99}}").Error.Should().Be("欄位 99 不存在或未啟用");

    [Fact]
    public void Rule_NotFilterableField_Allowed() =>
        P("{\"field\":5,\"op\":\"eq\",\"value\":\"x\"}").Error.Should().BeNull();

    [Fact]
    public void Rule_TextareaIsEmpty_Allowed()
    {
        P("{\"field\":4,\"op\":\"isEmpty\"}").Error.Should().BeNull();
        P("{\"field\":4,\"op\":\"isNotEmpty\"}").Error.Should().BeNull();
        P("{\"field\":4,\"op\":\"contains\",\"value\":\"x\"}").Error.Should().Be("欄位 4 不支援運算子 contains");
    }

    [Fact]
    public void Rule_WithinNextDays_Supported() =>
        P("{\"field\":1,\"op\":\"withinNextDays\",\"value\":7}").Error.Should().BeNull();

    [Fact]
    public void ListFilter_FieldRef_StillRejected() =>
        P("{\"field\":1,\"op\":\"gte\",\"value\":{\"fieldRef\":2}}", ConditionUsage.ListFilter)
            .Error.Should().Be("列表篩選不支援 fieldRef");
}
