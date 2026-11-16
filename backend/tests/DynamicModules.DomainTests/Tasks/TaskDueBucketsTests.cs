using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Tasks;

/// <summary>到期區間的邊界（看板設計 §5.1、§5.3）。2026-11-04 是週三。</summary>
public class TaskDueBucketsTests
{
    private static readonly DateOnly Wed = new(2026, 11, 4);

    [Theory]
    [InlineData(DayOfWeek.Monday, "2026-11-08")]
    [InlineData(DayOfWeek.Tuesday, "2026-11-09")]
    [InlineData(DayOfWeek.Wednesday, "2026-11-10")]
    [InlineData(DayOfWeek.Thursday, "2026-11-04")]
    [InlineData(DayOfWeek.Friday, "2026-11-05")]
    [InlineData(DayOfWeek.Saturday, "2026-11-06")]
    [InlineData(DayOfWeek.Sunday, "2026-11-07")]
    public void Buckets_ForEveryWeekStart(DayOfWeek weekStart, string expectedWeekEnd)
    {
        var weekEnd = TaskDates.WeekEnd(Wed, weekStart);

        weekEnd.Should().Be(DateOnly.Parse(expectedWeekEnd));
        TaskDueBuckets.Of(Wed.AddDays(-1), Wed, weekEnd).Should().Be(TaskDueBuckets.Overdue);
        TaskDueBuckets.Of(Wed, Wed, weekEnd).Should().Be(TaskDueBuckets.Today);
        TaskDueBuckets.Of(weekEnd.AddDays(1), Wed, weekEnd).Should().Be(TaskDueBuckets.Later);
        TaskDueBuckets.Of(null, Wed, weekEnd).Should().Be(TaskDueBuckets.None);
        if (weekEnd > Wed)
        {
            TaskDueBuckets.Of(Wed.AddDays(1), Wed, weekEnd).Should().Be(TaskDueBuckets.ThisWeek);
            TaskDueBuckets.Of(weekEnd, Wed, weekEnd).Should().Be(TaskDueBuckets.ThisWeek);
        }
        // 每一天都恰好落在一個區間，且與資料庫條件一致
        for (var d = Wed.AddDays(-3); d <= Wed.AddDays(14); d = d.AddDays(1))
        {
            var bucket = TaskDueBuckets.Of(d, Wed, weekEnd);
            var item = new Domain.Entities.Dynamic.TaskItem { DueDate = d };
            TaskDueBuckets.Keys.Where(k => TaskDueBuckets.Condition(k, Wed, weekEnd).Compile()(item)).Should().Equal(bucket);
        }
    }

    [Fact]
    public void TodayIsWeekEnd_ThisWeekEmpty_TargetRejected()
    {
        var sunday = new DateOnly(2026, 11, 8);
        var weekEnd = TaskDates.WeekEnd(sunday, DayOfWeek.Monday);

        weekEnd.Should().Be(sunday);
        TaskDueBuckets.DropBlock(TaskDueBuckets.ThisWeek, sunday, weekEnd).Should().Be(DueTargetError.ThisWeekEmpty);
        TaskDueBuckets.TargetDate(TaskDueBuckets.ThisWeek, sunday, weekEnd, null).Error.Should().Be(DueTargetError.ThisWeekEmpty);
        TaskDueBuckets.TargetDate(TaskDueBuckets.Today, sunday, weekEnd, null).Date.Should().Be(sunday);
        TaskDueBuckets.TargetDate(TaskDueBuckets.Overdue, sunday, weekEnd, null).Error.Should().Be(DueTargetError.OverdueNotAllowed);
        TaskDueBuckets.TargetDate(TaskDueBuckets.ThisWeek, Wed, TaskDates.WeekEnd(Wed, DayOfWeek.Monday), null).Date
            .Should().Be(new DateOnly(2026, 11, 8));
    }

    [Fact]
    public void LaterDefault_IsNextWeekEnd()
    {
        var weekEnd = TaskDates.WeekEnd(Wed, DayOfWeek.Monday);

        TaskDueBuckets.LaterDefault(weekEnd).Should().Be(new DateOnly(2026, 11, 15));
        TaskDueBuckets.TargetDate(TaskDueBuckets.Later, Wed, weekEnd, null).Error.Should().Be(DueTargetError.LaterNeedsDate);
        TaskDueBuckets.TargetDate(TaskDueBuckets.Later, Wed, weekEnd, weekEnd).Error.Should().Be(DueTargetError.LaterNotAfterWeekEnd);
        TaskDueBuckets.TargetDate(TaskDueBuckets.Later, Wed, weekEnd, weekEnd.AddDays(1)).Date.Should().Be(weekEnd.AddDays(1));
        TaskDueBuckets.TargetDate(TaskDueBuckets.None, Wed, weekEnd, null).Should().Be(new DueTarget(DueTargetError.None, null));
        TaskDueBuckets.TargetDate("soon", Wed, weekEnd, null).Error.Should().Be(DueTargetError.UnknownColumn);
    }

    /// <summary>搬家前（GetMyTasksHandler.WeekEnd）是 ReportDates.WeekStart＋6，搬家後結果一樣。</summary>
    [Fact]
    public void WeekEnd_SameAsBefore()
    {
        for (var d = new DateOnly(2026, 12, 20); d <= new DateOnly(2027, 1, 10); d = d.AddDays(1))
            foreach (var ws in Enum.GetValues<DayOfWeek>())
                TaskDates.WeekEnd(d, ws).Should().Be(ReportDates.WeekStart(d, ws).AddDays(6));
    }
}
