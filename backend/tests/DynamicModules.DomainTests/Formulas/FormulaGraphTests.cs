using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Entities.Dynamic;
using Xunit;
using static DynamicModules.DomainTests.Formulas.FormulaTestKit;

namespace DynamicModules.DomainTests.Formulas;

public class FormulaGraphTests
{
    [Fact]
    public void EvaluationOrder_DependencyFirst()
    {
        // 8 排在 6 前面宣告，順序仍須 6 先
        var m = Mod(Fld(1, FieldType.Integer), Fld(2, FieldType.Currency),
            Formula(8, "{6} + 1"), Formula(6, "{1}*{2}"));
        var ids = FormulaGraph.EvaluationOrder(m).Select(f => f.Id).ToList();
        Assert.Equal([6L, 8L], ids);
    }

    [Fact]
    public void EvaluationOrder_SkipsInactive()
    {
        var m = Mod(Fld(1, FieldType.Integer), Formula(6, "{1}"),
            Fld(8, FieldType.Formula, new() { Formula = "{6}" }, FieldStatus.Inactive));
        Assert.Equal([6L], FormulaGraph.EvaluationOrder(m).Select(f => f.Id).ToList());
    }

    [Fact]
    public void HasCycle_SelfAndIndirect_True()
    {
        var m = Mod(Fld(1, FieldType.Integer), Formula(6, "{1}"), Formula(8, "{6}"));
        Assert.True(FormulaGraph.HasCycle(m, 6, new HashSet<long> { 6 }));
        Assert.True(FormulaGraph.HasCycle(m, 6, new HashSet<long> { 8 }));
    }

    [Fact]
    public void HasCycle_Unrelated_False()
    {
        var m = Mod(Fld(1, FieldType.Integer), Formula(6, "{1}"), Formula(8, "{6}"));
        Assert.False(FormulaGraph.HasCycle(m, 8, new HashSet<long> { 1, 6 }));
        Assert.False(FormulaGraph.HasCycle(m, 9, new HashSet<long> { 8 }));
    }
}
