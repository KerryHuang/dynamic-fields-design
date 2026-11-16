using DynamicModules.Domain.Formulas;
using Xunit;
using static DynamicModules.DomainTests.Formulas.FormulaTestKit;

namespace DynamicModules.DomainTests.Formulas;

public class FormulaParserTests
{
    [Theory]
    [InlineData("", "運算式是空的")]
    [InlineData("{1} +", "語法錯誤")]
    [InlineData("{99}", "欄位 99 不存在或未啟用")]
    [InlineData("{5} + 1", "欄位 5 的型別不能用在公式")]
    [InlineData("{7}", "欄位 7 的型別不能用在公式")]
    [InlineData("{4} + 1", "日期不能直接加減，請用 DATEADD")]
    [InlineData("IF({1}, 1, 2)", "IF 的第一個參數必須是比較")]
    [InlineData("IF({1} > 0, 1, TODAY())", "IF 兩個結果的型別必須相同")]
    [InlineData("ROUND({1}, 7)", "ROUND 的位數必須是 0 到 6 的整數")]
    [InlineData("FOO(1)", "不支援的函式 FOO")]
    [InlineData("{1} > 2", "比較只能用在 IF 裡")]
    public void Parse_Errors(string expr, string error)
    {
        var r = FormulaParser.Parse(expr, Standard());
        Assert.Null(r.Node);
        Assert.Equal(error, r.Error);
    }

    [Fact]
    public void Parse_NullExpression_IsEmpty() =>
        Assert.Equal("運算式是空的", FormulaParser.Parse(null, Standard()).Error);

    [Fact]
    public void Parse_InactiveField_NotFound()
    {
        var m = Mod(Fld(1, DynamicModules.Domain.Entities.Dynamic.FieldType.Integer, status: DynamicModules.Domain.Entities.Dynamic.FieldStatus.Inactive));
        Assert.Equal("欄位 1 不存在或未啟用", FormulaParser.Parse("{1}", m).Error);
    }

    [Fact]
    public void Parse_TooLong() =>
        Assert.Equal("運算式太長", FormulaParser.Parse(new string('1', 1001), Standard()).Error);

    [Fact]
    public void Parse_TooDeep() =>
        Assert.Equal("運算式太複雜", FormulaParser.Parse(new string('(', 21) + "1" + new string(')', 21), Standard()).Error);

    [Fact]
    public void Parse_DepthTwenty_Ok() =>
        Assert.Null(FormulaParser.Parse(new string('(', 19) + "1" + new string(')', 19), Standard()).Error);

    [Theory]
    [InlineData("ROUND({2} * {3}, 0)", FormulaValueType.Number, new long[] { 2, 3 })]
    [InlineData("DATEDIFF({4}, TODAY())", FormulaValueType.Number, new long[] { 4 })]
    [InlineData("DATEADD({4}, 7)", FormulaValueType.Date, new long[] { 4 })]
    [InlineData("{6} + 1", FormulaValueType.Number, new long[] { 6 })]
    [InlineData("round(-{1} / 2, 2)", FormulaValueType.Number, new long[] { 1 })]
    [InlineData("IF({1} >= 10, {2}, 0)", FormulaValueType.Number, new long[] { 1, 2 })]
    public void Parse_Success(string expr, FormulaValueType type, long[] refs)
    {
        var r = FormulaParser.Parse(expr, Standard());
        Assert.Null(r.Error);
        Assert.NotNull(r.Node);
        Assert.Equal(type, r.Type);
        Assert.True(r.FieldRefs.SetEquals(refs));
    }

    [Fact]
    public void Parse_DateFormula_IsDateReferenceable()
    {
        var m = Mod(Fld(4, DynamicModules.Domain.Entities.Dynamic.FieldType.Date), Formula(9, "DATEADD({4}, 1)", "date"));
        var r = FormulaParser.Parse("DATEDIFF({9}, TODAY())", m);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData("currency", FormulaValueType.Number)]
    [InlineData("number", FormulaValueType.Number)]
    [InlineData("date", FormulaValueType.Date)]
    public void FormulaParser_RollupField_TypedByResultType(string resultType, FormulaValueType type)
    {
        var m = Mod(Fld(9, DynamicModules.Domain.Entities.Dynamic.FieldType.Rollup,
            new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = resultType }));
        var r = FormulaParser.Parse("{9}", m);
        Assert.Null(r.Error);
        Assert.Equal(type, r.Type);
    }
}
