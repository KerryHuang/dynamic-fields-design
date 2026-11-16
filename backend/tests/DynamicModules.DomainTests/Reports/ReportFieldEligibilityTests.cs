using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using FluentAssertions;
using static DynamicModules.DomainTests.Formulas.FormulaTestKit;

namespace DynamicModules.DomainTests.Reports;

public class ReportFieldEligibilityTests
{
    [Fact]
    public void Excluded_Rollup_And_IndirectFormula()
    {
        // 6 引用 7、7 引用彙總 9；8 只引用一般欄位
        var m = Mod(Fld(1, FieldType.Integer), Fld(9, FieldType.Rollup),
            Formula(6, "{7} + 1"), Formula(7, "{9} * 2"), Formula(8, "{1} * 2"));
        ReportFieldEligibility.ExcludedFieldIds(m).Should().BeEquivalentTo(new long[] { 9, 7, 6 });
    }

    [Theory]
    [InlineData(FieldType.Text, true)]
    [InlineData(FieldType.Enum, true)]
    [InlineData(FieldType.Date, true)]
    [InlineData(FieldType.Textarea, false)]
    public void CanGroup(FieldType t, bool expected) => ReportFieldEligibility.CanGroup(Fld(1, t)).Should().Be(expected);

    [Theory]
    [InlineData(FieldType.Integer, true)]
    [InlineData(FieldType.Decimal, true)]
    [InlineData(FieldType.Currency, true)]
    [InlineData(FieldType.Percent, true)]
    [InlineData(FieldType.Date, false)]
    [InlineData(FieldType.Text, false)]
    public void CanSum(FieldType t, bool expected) => ReportFieldEligibility.CanSum(Fld(1, t)).Should().Be(expected);

    [Fact]
    public void Formula_ResultType_DecidesSumAndMinMax()
    {
        ReportFieldEligibility.CanSum(Formula(1, "{2}")).Should().BeTrue();
        ReportFieldEligibility.CanSum(Formula(1, "{2}", "currency")).Should().BeTrue();
        ReportFieldEligibility.CanSum(Formula(1, "{2}", "date")).Should().BeFalse();
        ReportFieldEligibility.CanMinMax(Formula(1, "{2}", "date")).Should().BeTrue();
        ReportFieldEligibility.CanMinMax(Formula(1, "{2}", "text")).Should().BeFalse();
    }

    [Theory]
    [InlineData(FieldType.Integer, true)]
    [InlineData(FieldType.Date, true)]
    [InlineData(FieldType.DateTime, true)]
    [InlineData(FieldType.Text, false)]
    public void CanMinMax(FieldType t, bool expected) => ReportFieldEligibility.CanMinMax(Fld(1, t)).Should().Be(expected);
}
