using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class DateTimeHandlerTests
{
    [Fact]
    public void DateHandler_AcceptsIsoDate()
    {
        var r = Run(FieldType.Date, "\"2026-11-03\"");
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetString().Should().Be("2026-11-03");
    }

    [Theory]
    [InlineData("\"2026/11/03\"")]
    [InlineData("\"2026-13-01\"")]
    [InlineData("\"2026-11-03T00:00:00\"")]
    [InlineData("20261103")]
    public void DateHandler_RejectsNonIsoDate(string json)
    {
        var r = Run(FieldType.Date, json);
        (r.IsValid, r.Error).Should().Be((false, "日期格式必須是 yyyy-MM-dd"));
    }

    [Fact]
    public void DateHandler_Null_IsOkNull() => Run(FieldType.Date, "null").Value.Should().BeNull();

    [Fact]
    public void DateTimeHandler_NormalizesToTaipeiOffset()
    {
        var r = Run(FieldType.DateTime, "\"2026-11-03T01:00:00Z\"");
        r.IsValid.Should().BeTrue();
        r.Value!.Value.GetString().Should().Be("2026-11-03T09:00:00+08:00");
    }

    [Fact]
    public void DateTimeHandler_ConvertsOtherOffsets_AndCrossesMidnight()
    {
        Run(FieldType.DateTime, "\"2026-11-03T20:30+00:00\"").Value!.Value.GetString()
            .Should().Be("2026-11-04T04:30:00+08:00");
        Run(FieldType.DateTime, "\"2026-11-03T09:00:00.123+09:00\"").Value!.Value.GetString()
            .Should().Be("2026-11-03T08:00:00+08:00");
    }

    [Theory]
    [InlineData("\"2026-11-03T01:00:00\"")]
    [InlineData("\"2026-11-03\"")]
    [InlineData("\"nope\"")]
    [InlineData("5")]
    public void DateTimeHandler_RejectsMissingOffsetOrBadFormat(string json)
    {
        var r = Run(FieldType.DateTime, json);
        (r.IsValid, r.Error).Should().Be((false, "日期時間格式錯誤"));
    }

    [Fact]
    public void TimeHandler_ValidAndInvalid()
    {
        Run(FieldType.Time, "\"09:30\"").Value!.Value.GetString().Should().Be("09:30");
        foreach (var bad in new[] { "\"9:30\"", "\"25:00\"", "\"09:30:00\"", "1" })
            Run(FieldType.Time, bad).Error.Should().Be("時間格式必須是 HH:mm");
        Run(FieldType.Time, "null").IsValid.Should().BeTrue();
    }
}
