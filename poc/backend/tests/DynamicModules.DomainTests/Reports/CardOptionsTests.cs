using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class CardOptionsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void PreviousWindows_ThisMonth_IsPreviousFullMonths()
    {
        var windows = ReportCardMath.PreviousWindows(new ReportDateRange("thisMonth", null), D(2026, 11, 15), 3);

        windows.Should().Equal((D(2026, 10, 1), D(2026, 10, 31)), (D(2026, 9, 1), D(2026, 9, 30)), (D(2026, 8, 1), D(2026, 8, 31)));
    }

    [Fact]
    public void PreviousWindows_LastDays30_IsPrevious30DayWindows()
    {
        var windows = ReportCardMath.PreviousWindows(new ReportDateRange("lastDays", 30), D(2026, 11, 30), 2);

        windows.Should().Equal((D(2026, 10, 2), D(2026, 10, 31)), (D(2026, 9, 2), D(2026, 10, 1)));
    }

    [Fact]
    public void PreviousWindows_LastMonths3_ShiftsByThreeMonths()
    {
        var windows = ReportCardMath.PreviousWindows(new ReportDateRange("lastMonths", 3), D(2026, 11, 15), 2);

        windows.Should().Equal((D(2026, 6, 1), D(2026, 8, 31)), (D(2026, 3, 1), D(2026, 5, 31)));
    }

    [Fact]
    public void PreviousWindows_WeekAndYearAndToday()
    {
        ReportCardMath.PreviousWindows(new ReportDateRange("thisWeek", null), D(2026, 11, 5), 1)
            .Should().Equal((D(2026, 10, 26), D(2026, 11, 1)));
        ReportCardMath.PreviousWindows(new ReportDateRange("lastYear", null), D(2026, 11, 5), 1)
            .Should().Equal((D(2024, 1, 1), D(2024, 12, 31)));
        ReportCardMath.PreviousWindows(new ReportDateRange("today", null), D(2026, 3, 1), 2)
            .Should().Equal((D(2026, 2, 28), D(2026, 2, 28)), (D(2026, 2, 27), D(2026, 2, 27)));
    }

    [Fact]
    public void Compare_DeltaPercentAgainstAverage()
    {
        var (average, delta) = ReportCardMath.Compare(150m, [100m, 100m, 100m]);

        average.Should().Be(100m);
        delta.Should().Be(50m);
    }

    [Fact]
    public void Compare_PreviousAverageZero_NoDelta()
    {
        var (average, delta) = ReportCardMath.Compare(10m, [0m, 0m]);

        average.Should().Be(0m);
        delta.Should().BeNull();
    }

    [Fact]
    public void Compare_NoPreviousValues_AllNull()
    {
        ReportCardMath.Compare(10m, []).Should().Be((null, null));
    }

    [Fact]
    public void Cumulative_DenominatorIsAllGroups_NotTopN()
    {
        // 全部分組 50、30、10、10 共 100；只顯示前 2 名時累計仍是占 100 的比例
        var percents = ReportCardMath.CumulativePercent([50m, 30m], allGroupsTotal: 100m);

        percents.Should().Equal(50m, 80m);
    }

    [Fact]
    public void Cumulative_NullValueCarriesAndZeroTotalIsNull()
    {
        ReportCardMath.CumulativePercent([40m, null, 20m], 100m).Should().Equal(40m, 40m, 60m);
        ReportCardMath.CumulativePercent([1m], 0m).Should().Equal(new decimal?[] { null });
    }
}
