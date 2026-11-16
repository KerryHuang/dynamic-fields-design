using DynamicModules.Domain.AutoNumbers;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.AutoNumbers;

public class AutoNumberFormatTests
{
    [Theory]
    [InlineData("REP-{yyyyMM}-{0000}", "2026-11-03", 7, "202611", "REP-202611-0007")]
    [InlineData("SO{yy}{MM}{000}", "2026-01-15", 12, "202601", "SO2601012")]
    [InlineData("Y{yyyy}-{00}", "2026-11-03", 123, "2026", "Y2026-123")]
    [InlineData("NO-{000000}", "2026-11-03", 1, "", "NO-000001")]
    public void Render_And_Period(string fmt, string date, long n, string period, string expected)
    {
        AutoNumberFormat.TryParse(fmt, out var parsed).Should().BeTrue();
        var now = DateTime.Parse(date);
        parsed!.PeriodKey(now).Should().Be(period);
        parsed!.Render(now, n).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NO-{yyyy}")]
    [InlineData("{000}-{00}")]
    [InlineData("{00000000000}")]
    [InlineData("A-{dd}-{000}")]
    [InlineData("A-{000")]
    public void TryParse_Invalid_ReturnsFalse(string? fmt) =>
        AutoNumberFormat.TryParse(fmt, out _).Should().BeFalse();

    [Fact]
    public void TryParse_Over50Chars_ReturnsFalse() =>
        AutoNumberFormat.TryParse(new string('A', 48) + "{0}", out _).Should().BeFalse();

    [Fact]
    public void Render_NumberLongerThanDigits_NotTruncated()
    {
        AutoNumberFormat.TryParse("N{00}", out var parsed).Should().BeTrue();
        parsed!.Render(new DateTime(2026, 1, 1), 12345).Should().Be("N12345");
    }
}
