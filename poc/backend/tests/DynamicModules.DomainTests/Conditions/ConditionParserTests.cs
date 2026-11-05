using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.Conditions;

public class ConditionParserTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();

    private static Field Fld(long id, FieldType type, bool filterable = true, FieldStatus status = FieldStatus.Active,
        params (long Id, bool Active, int Seq)[] options) =>
        new()
        {
            Id = id, Key = $"f_{id}", Type = type, Status = status,
            Config = new FieldConfig { IsFilterable = filterable },
            Options = options.Select(o => new FieldOption { Id = o.Id, Active = o.Active, Sequence = o.Seq }).ToList()
        };

    private static Module Mod() => new()
    {
        Blocks =
        [
            new Block
            {
                Fields =
                [
                    Fld(1, FieldType.Text), Fld(2, FieldType.Integer), Fld(3, FieldType.Date),
                    Fld(4, FieldType.Enum, true, FieldStatus.Active, (10, true, 1), (11, false, 2)),
                    Fld(5, FieldType.MultiEnum, true, FieldStatus.Active, (20, true, 1), (21, true, 2)),
                    Fld(6, FieldType.Textarea, true), Fld(7, FieldType.Text, false),
                    Fld(8, FieldType.Text, true, FieldStatus.Inactive), Fld(9, FieldType.User),
                    Fld(12, FieldType.MultiEnum, false, FieldStatus.Active, (30, true, 1))
                ]
            }
        ]
    };

    private static ConditionParseResult P(string json, params string[] users) =>
        ConditionParser.Parse(J(json), Mod(), Registry, ConditionUsage.ListFilter, users.ToHashSet());

    private static void Err(string json, string expected) =>
        P(json).Should().Match<ConditionParseResult>(r => r.Node == null && r.Error == expected);

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{}")]
    [InlineData("{\"all\":{}}")]
    [InlineData("{\"all\":[],\"any\":[]}")]
    [InlineData("{\"field\":\"1\",\"op\":\"eq\",\"value\":\"a\"}")]
    [InlineData("{\"field\":1,\"op\":5}")]
    [InlineData("{\"field\":1,\"op\":\"eq\",\"value\":\"a\",\"extra\":1}")]
    public void Parse_MalformedShape_ReturnsFormatError(string json) => Err(json, "篩選條件格式錯誤");

    [Fact]
    public void Parse_21Conditions_ReturnsLimitError()
    {
        var c = string.Join(",", Enumerable.Repeat("{\"field\":1,\"op\":\"isEmpty\"}", 21));
        Err($"{{\"all\":[{c}]}}", "篩選條件最多 20 個");
        P($"{{\"all\":[{string.Join(",", Enumerable.Repeat("{\"field\":1,\"op\":\"isEmpty\"}", 20))}]}}")
            .Error.Should().BeNull();
    }

    [Fact]
    public void Parse_Depth4_ReturnsDepthError()
    {
        const string leaf = "{\"field\":1,\"op\":\"isEmpty\"}";
        P($"{{\"all\":[{{\"any\":[{{\"all\":[{leaf}]}}]}}]}}").Error.Should().BeNull();
        Err($"{{\"all\":[{{\"any\":[{{\"all\":[{{\"any\":[{leaf}]}}]}}]}}]}}", "篩選條件最多 3 層");
    }

    [Fact]
    public void Parse_UnknownOrInactiveField_ReturnsNotFound()
    {
        Err("{\"field\":999,\"op\":\"isEmpty\"}", "欄位 999 不存在或未啟用");
        Err("{\"field\":8,\"op\":\"isEmpty\"}", "欄位 8 不存在或未啟用");
    }

    [Fact]
    public void Parse_NotFilterable_ReturnsError()
    {
        Err("{\"field\":7,\"op\":\"isEmpty\"}", "欄位 7 未開放篩選");
        Err("{\"field\":6,\"op\":\"isEmpty\"}", "欄位 6 未開放篩選");
        Err("{\"field\":12,\"op\":\"in\",\"value\":[\"opt_30\"]}", "欄位 12 未開放篩選");
    }

    [Fact]
    public void Parse_UnsupportedOperator_ReturnsError()
    {
        Err("{\"field\":4,\"op\":\"gt\",\"value\":\"opt_10\"}", "欄位 4 不支援運算子 gt");
        Err("{\"field\":1,\"op\":\"bogus\",\"value\":\"a\"}", "欄位 1 不支援運算子 bogus");
        Err("{\"field\":5,\"op\":\"eq\",\"value\":\"opt_20\"}", "欄位 5 不支援運算子 eq");
    }

    [Fact]
    public void Parse_FieldRef_ReturnsError()
    {
        Err("{\"field\":2,\"op\":\"eq\",\"value\":{\"fieldRef\":13}}", "列表篩選不支援 fieldRef");
        Err("{\"field\":2,\"op\":\"eq\",\"fieldRef\":13}", "列表篩選不支援 fieldRef");
    }

    [Fact]
    public void Parse_SingleConditionAtRoot_IsAllowed()
    {
        var r = P("{\"field\":2,\"op\":\"gt\",\"value\":5}");
        var c = r.Node.Should().BeOfType<FieldCondition>().Subject;
        c.Op.Should().Be(FilterOperator.Gt);
        c.Field.Id.Should().Be(2);
        c.Values.Should().ContainSingle().Which.NumberValue.Should().Be(5);
        c.Days.Should().BeNull();
    }

    [Fact]
    public void Parse_NestedGroups_BuildsTree()
    {
        var r = P("""
            {"all":[{"field":1,"op":"eq","value":"a"},
                    {"any":[{"field":2,"op":"gt","value":1},{"field":2,"op":"isEmpty"}]}]}
            """);
        var root = r.Node.Should().BeOfType<ConditionGroup>().Subject;
        root.All.Should().BeTrue();
        root.Children.Should().HaveCount(2);
        var any = root.Children[1].Should().BeOfType<ConditionGroup>().Subject;
        any.All.Should().BeFalse();
        any.Children.Should().AllBeOfType<FieldCondition>();
        ((FieldCondition)any.Children[1]).Values.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyAll_ReturnsNullNode()
    {
        var r = P("{\"all\":[]}");
        r.Node.Should().BeNull();
        r.Error.Should().BeNull();
    }

    [Fact]
    public void Parse_InactiveOptionValue_IsAllowedForFilter()
    {
        var r = P("{\"field\":4,\"op\":\"eq\",\"value\":\"opt_11\"}");
        var c = r.Node.Should().BeOfType<FieldCondition>().Subject;
        c.Values.Should().ContainSingle().Which.TextValue.Should().Be("opt_11");
        Err("{\"field\":4,\"op\":\"eq\",\"value\":\"opt_99\"}", "欄位 4 的條件值格式錯誤");
    }

    [Fact]
    public void Parse_MultiEnumIn_EachElementIsSingleOption()
    {
        var r = P("{\"field\":5,\"op\":\"in\",\"value\":[\"opt_20\",\"opt_21\"]}");
        var c = r.Node.Should().BeOfType<FieldCondition>().Subject;
        c.Values.Select(v => v.TextValue).Should().Equal("opt_20", "opt_21");
        Err("{\"field\":5,\"op\":\"in\",\"value\":[[\"opt_20\"]]}", "欄位 5 的條件值格式錯誤");
        Err("{\"field\":5,\"op\":\"in\",\"value\":[\"opt_99\"]}", "欄位 5 的條件值格式錯誤");
    }

    [Fact]
    public void Parse_UserIn_ChecksUserIds()
    {
        P("{\"field\":9,\"op\":\"eq\",\"value\":\"u1\"}", "u1").Node.Should().BeOfType<FieldCondition>();
        Err("{\"field\":9,\"op\":\"eq\",\"value\":\"u1\"}", "欄位 9 的條件值格式錯誤");
    }

    [Fact]
    public void Parse_DateNotIso_ReturnsValueError()
    {
        Err("{\"field\":3,\"op\":\"eq\",\"value\":\"2026/11/03\"}", "欄位 3 的條件值格式錯誤");
        P("{\"field\":3,\"op\":\"eq\",\"value\":\"2026-11-03\"}").Node.Should().BeOfType<FieldCondition>();
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[1,2,3]")]
    [InlineData("5")]
    [InlineData("[1,\"x\"]")]
    public void Parse_BetweenNeedsExactlyTwo(string value) =>
        Err($"{{\"field\":2,\"op\":\"between\",\"value\":{value}}}", "欄位 2 的條件值格式錯誤");

    [Fact]
    public void Parse_Between_ReturnsTwoValues() =>
        ((FieldCondition)P("{\"field\":2,\"op\":\"between\",\"value\":[1,9]}").Node!).Values
        .Select(v => v.NumberValue).Should().Equal(1, 9);

    [Fact]
    public void Parse_InEmptyOrOver50_ReturnsValueError()
    {
        Err("{\"field\":1,\"op\":\"in\",\"value\":[]}", "欄位 1 的條件值格式錯誤");
        Err("{\"field\":1,\"op\":\"notIn\",\"value\":\"a\"}", "欄位 1 的條件值格式錯誤");
        var many = string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"v{i}\""));
        Err($"{{\"field\":1,\"op\":\"in\",\"value\":[{many}]}}", "欄位 1 的條件值格式錯誤");
        var ok = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"v{i}\""));
        ((FieldCondition)P($"{{\"field\":1,\"op\":\"in\",\"value\":[{ok}]}}").Node!).Values.Should().HaveCount(50);
    }

    [Fact]
    public void Parse_ContainsEmptyOrOver440_ReturnsValueError()
    {
        Err("{\"field\":1,\"op\":\"contains\",\"value\":\"\"}", "欄位 1 的條件值格式錯誤");
        Err("{\"field\":1,\"op\":\"contains\",\"value\":5}", "欄位 1 的條件值格式錯誤");
        Err($"{{\"field\":1,\"op\":\"startsWith\",\"value\":\"{new string('a', 441)}\"}}", "欄位 1 的條件值格式錯誤");
        var c = (FieldCondition)P($"{{\"field\":1,\"op\":\"contains\",\"value\":\" {new string('a', 439)}\"}}").Node!;
        c.Values.Should().ContainSingle().Which.TextValue.Should().HaveLength(440).And.StartWith(" ");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    public void Parse_WithinDaysOutOfRange_ReturnsValueError(string value) =>
        Err($"{{\"field\":3,\"op\":\"withinNextDays\",\"value\":{value}}}", "欄位 3 的條件值格式錯誤");

    [Fact]
    public void Parse_WithinDays_PutsDaysAndNoValues()
    {
        var c = (FieldCondition)P("{\"field\":3,\"op\":\"withinLastDays\",\"value\":3650}").Node!;
        c.Days.Should().Be(3650);
        c.Values.Should().BeEmpty();
        ((FieldCondition)P("{\"field\":3,\"op\":\"withinNextDays\",\"value\":0}").Node!).Days.Should().Be(0);
    }

    [Fact]
    public void Parse_IsEmptyWithValue_ReturnsValueError() =>
        Err("{\"field\":1,\"op\":\"isEmpty\",\"value\":1}", "欄位 1 的條件值格式錯誤");

    [Fact]
    public void Parse_MissingValue_ReturnsValueError() =>
        Err("{\"field\":1,\"op\":\"eq\"}", "欄位 1 的條件值格式錯誤");

    private static ConditionParseResult PComputed(string json)
    {
        Field Comp(long id, FieldType type, string? resultType) => new()
        {
            Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active,
            Config = new FieldConfig { IsFilterable = true, ResultType = resultType }
        };
        var module = new Module
        {
            Blocks = [new Block { Fields = [Comp(21, FieldType.Formula, "number"), Comp(22, FieldType.Formula, "date"), Comp(23, FieldType.AutoNumber, null)] }]
        };
        return ConditionParser.Parse(J(json), module, Registry, ConditionUsage.ListFilter, new HashSet<string>());
    }

    [Fact]
    public void Parse_NumberFormula_Gte_UsesNumberValue()
    {
        var c = (FieldCondition)PComputed("{\"field\":21,\"op\":\"gte\",\"value\":100}").Node!;
        c.Values.Single().NumberValue.Should().Be(100);
    }

    [Fact]
    public void Parse_DateFormula_WithinNextDays_Allowed() =>
        PComputed("{\"field\":22,\"op\":\"withinNextDays\",\"value\":7}").Error.Should().BeNull();

    [Fact]
    public void Parse_NumberFormula_WithinNextDays_Rejected() =>
        PComputed("{\"field\":21,\"op\":\"withinNextDays\",\"value\":7}").Error.Should().Be("欄位 21 不支援運算子 withinNextDays");

    [Fact]
    public void Parse_DateFormula_Eq_UsesDateValue() =>
        ((FieldCondition)PComputed("{\"field\":22,\"op\":\"eq\",\"value\":\"2026-11-03\"}").Node!)
            .Values.Single().DateValue.Should().Be(new DateTime(2026, 11, 3));

    [Fact]
    public void Parse_AutoNumber_Contains_Allowed() =>
        PComputed("{\"field\":23,\"op\":\"eq\",\"value\":\"REP-1\"}").Node.Should().NotBeNull();
}
