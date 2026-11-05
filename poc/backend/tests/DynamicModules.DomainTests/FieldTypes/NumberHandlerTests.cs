using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class NumberHandlerTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("3.0", 3)]
    [InlineData("-7", -7)]
    public void IntegerHandler_AcceptsWholeNumbers(string json, long expected)
    {
        var r = Run(FieldType.Integer, json);
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetInt64().Should().Be(expected);
    }

    [Theory]
    [InlineData("3.5")]
    [InlineData("\"3\"")]
    [InlineData("true")]
    public void IntegerHandler_RejectsNonInteger(string json)
    {
        var r = Run(FieldType.Integer, json);
        (r.IsValid, r.Error).Should().Be((false, "必須是整數"));
    }

    [Fact]
    public void IntegerHandler_Null_IsOkNull()
    {
        Run(FieldType.Integer, "null").Should().Be(new DynamicModules.Domain.FieldTypes.FieldValueResult(true, null, null));
        Run(FieldType.Integer, null).Value.Should().BeNull();
    }

    [Theory]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Currency)]
    [InlineData(FieldType.Percent)]
    public void DecimalHandler_AcceptsNumberWithinScale(FieldType type)
    {
        var r = Run(type, "12.5");
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetDecimal().Should().Be(12.5m);
        Run(type, "1.50").IsValid.Should().BeTrue();
        Run(type, "null").IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"1,000\"")]
    [InlineData("\"3.5\"")]
    public void DecimalHandler_RejectsStringAndLocaleFormatted(string json)
    {
        var r = Run(FieldType.Decimal, json);
        (r.IsValid, r.Error).Should().Be((false, "必須是數字"));
    }

    [Fact]
    public void DecimalHandler_ScaleExceeded_ReturnsError()
    {
        var r = Run(FieldType.Decimal, "1.234", new FieldConfig { Scale = 2 });
        (r.IsValid, r.Error).Should().Be((false, "小數最多 2 位"));
    }

    [Fact]
    public void DecimalHandler_ScaleConfigAndDefault()
    {
        Run(FieldType.Decimal, "1.234", new FieldConfig { Scale = 3 }).IsValid.Should().BeTrue();
        Run(FieldType.Currency, "1.234").Error.Should().Be("小數最多 2 位");
        Run(FieldType.Percent, "0.123", new FieldConfig { Scale = 0 }).Error.Should().Be("小數最多 0 位");
    }
}
