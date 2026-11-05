using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class ReportDatesTests
{
    private static readonly DateOnly Basis = new(2026, 11, 5); // 週四

    private static (DateOnly, DateOnly) R(string kind, int? n = null, DateOnly? basis = null) =>
        ReportDates.Resolve(new ReportDateRange(kind, n), basis ?? Basis);

    private static (DateOnly, DateOnly) Range(int y1, int m1, int d1, int y2, int m2, int d2) =>
        (new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2));

    [Fact] public void Today() => R("today").Should().Be(Range(2026, 11, 5, 2026, 11, 5));
    [Fact] public void ThisWeek_StartsMonday() => R("thisWeek").Should().Be(Range(2026, 11, 2, 2026, 11, 8));
    [Fact] public void LastWeek() => R("lastWeek").Should().Be(Range(2026, 10, 26, 2026, 11, 1));
    [Fact] public void ThisMonth() => R("thisMonth").Should().Be(Range(2026, 11, 1, 2026, 11, 30));
    [Fact] public void LastMonth() => R("lastMonth").Should().Be(Range(2026, 10, 1, 2026, 10, 31));
    [Fact] public void ThisYear() => R("thisYear").Should().Be(Range(2026, 1, 1, 2026, 12, 31));
    [Fact] public void LastYear() => R("lastYear").Should().Be(Range(2025, 1, 1, 2025, 12, 31));
    [Fact] public void LastDays_IncludesToday() => R("lastDays", 7).Should().Be(Range(2026, 10, 30, 2026, 11, 5));

    [Fact]
    public void LastWeeks_IncludesThisWeek_EightWeeksTotal()
    {
        var (from, to) = R("lastWeeks", 8);
        from.Should().Be(new DateOnly(2026, 9, 14));
        to.Should().Be(new DateOnly(2026, 11, 8));
        ((to.DayNumber - from.DayNumber + 1) / 7).Should().Be(8);
    }

    [Fact] public void LastMonths_IncludesThisMonth_TwelveMonthsTotal() =>
        R("lastMonths", 12).Should().Be(Range(2025, 12, 1, 2026, 11, 30));

    [Fact] public void Year_Boundary() =>
        R("lastMonth", basis: new DateOnly(2027, 1, 1)).Should().Be(Range(2026, 12, 1, 2026, 12, 31));

    [Fact]
    public void Contains_Date_InclusiveBothEnds()
    {
        var range = Range(2026, 11, 1, 2026, 11, 30);
        ReportDates.Contains(range, Cell(new DateOnly(2026, 11, 1))).Should().BeTrue();
        ReportDates.Contains(range, Cell(new DateOnly(2026, 11, 30))).Should().BeTrue();
        ReportDates.Contains(range, Cell(new DateOnly(2026, 12, 1))).Should().BeFalse();
    }

    [Fact]
    public void DateTime_NextDayMidnight_NotInRange()
    {
        // 日期時間欄位已轉成台北牆上日期；隔天 00:00 的日期是隔天
        var range = Range(2026, 11, 5, 2026, 11, 5);
        ReportDates.Contains(range, Cell(new DateOnly(2026, 11, 6))).Should().BeFalse();
    }

    [Fact]
    public void Contains_NonOkOrNoDate_False()
    {
        var range = Range(2026, 11, 1, 2026, 11, 30);
        ReportDates.Contains(range, new ReportCell("empty", null, null, null, null)).Should().BeFalse();
        ReportDates.Contains(range, new ReportCell("ok", null, null, null, null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("day", "2026-11-05")]
    [InlineData("week", "2026-W45")]
    [InlineData("month", "2026-11")]
    [InlineData("quarter", "2026-Q4")]
    [InlineData("year", "2026")]
    public void BucketKey(string bucket, string expected) =>
        ReportDates.BucketKey(Basis, bucket).Should().Be(expected);

    [Fact]
    public void BucketKey_Week_UsesIsoYear() =>
        ReportDates.BucketKey(new DateOnly(2027, 1, 1), "week").Should().Be("2026-W53");

    private static ReportCell Cell(DateOnly d) => new("ok", null, null, d, null);
}
