using System.Text.Json;
using DynamicModules.Domain.Formulas;
using Xunit;
using static DynamicModules.DomainTests.Formulas.FormulaTestKit;

namespace DynamicModules.DomainTests.Formulas;

public class FormulaEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static JsonElement? Eval(string expr, Dictionary<string, JsonElement?>? values = null, int? scale = null)
    {
        var m = Standard();
        var p = FormulaParser.Parse(expr, m);
        Assert.Null(p.Error);
        return FormulaEvaluator.Evaluate(p.Node!, m, values ?? [], Today, scale);
    }

    [Fact]
    public void Evaluate_PercentAsFraction() =>
        Assert.Equal(50m, Eval("ROUND({2}*{3},0)", Values((2, "1000"), (3, "5")))!.Value.GetDecimal());

    [Fact]
    public void Evaluate_DecimalExact() =>
        Assert.Equal(0.3m, Eval("0.1+0.2")!.Value.GetDecimal());

    [Fact]
    public void Evaluate_NullOperand_ReturnsNull() =>
        Assert.Null(Eval("{1} + 1"));

    [Fact]
    public void Evaluate_DivideByZero_ReturnsNull() =>
        Assert.Null(Eval("1 / 0"));

    [Theory]
    [InlineData("10", 1)]
    [InlineData("9", 2)]
    public void Evaluate_IfBranch(string v, int expected) =>
        Assert.Equal(expected, Eval("IF({1} >= 10, 1, 2)", Values((1, v)))!.Value.GetInt32());

    [Fact]
    public void Evaluate_IfNullCondition_ReturnsNull() =>
        Assert.Null(Eval("IF({1} >= 10, 1, 2)"));

    [Fact]
    public void Evaluate_DateDiff() =>
        Assert.Equal(7, Eval("DATEDIFF({4}, TODAY())", Values((4, "\"2026-11-10\"")))!.Value.GetInt32());

    [Fact]
    public void Evaluate_DateAdd() =>
        Assert.Equal("2026-10-31", Eval("DATEADD({4}, -10)", Values((4, "\"2026-11-10\"")))!.Value.GetString());

    [Fact]
    public void Evaluate_ScaleRounding() =>
        Assert.Equal(3m, Eval("5 / 2", scale: 0)!.Value.GetDecimal());

    [Fact]
    public void Evaluate_DefaultScaleIsTwo() =>
        Assert.Equal(0.33m, Eval("1 / 3")!.Value.GetDecimal());

    [Fact]
    public void Evaluate_FormulaFieldUsesStoredValue() =>
        Assert.Equal(21m, Eval("{6} + 1", Values((6, "20")))!.Value.GetDecimal());

    [Fact]
    public void Evaluate_ReferencedFieldRemovedOrInactive_ReturnsNull()
    {
        var p = FormulaParser.Parse("{1} + 1", Standard());
        var values = Values((1, "5"));
        var removed = Mod(Fld(2, DynamicModules.Domain.Entities.Dynamic.FieldType.Currency));
        var inactive = Mod(Fld(1, DynamicModules.Domain.Entities.Dynamic.FieldType.Integer,
            status: DynamicModules.Domain.Entities.Dynamic.FieldStatus.Inactive));
        Assert.Null(FormulaEvaluator.Evaluate(p.Node!, removed, values, Today));
        Assert.Null(FormulaEvaluator.Evaluate(p.Node!, inactive, values, Today));
    }

    [Fact]
    public void Evaluate_DuplicateIdZeroFields_DoesNotThrow()
    {
        var m = Mod(Fld(0, DynamicModules.Domain.Entities.Dynamic.FieldType.Integer),
            Fld(0, DynamicModules.Domain.Entities.Dynamic.FieldType.Integer));
        Assert.Equal(3m, FormulaEvaluator.Evaluate(new NumberNode(3), m, new Dictionary<string, JsonElement?>(), Today)!.Value.GetDecimal());
    }
}
