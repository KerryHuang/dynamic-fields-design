using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using FluentAssertions;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.DomainTests.Reports;

public class TaskReportValuesTests
{
    private static readonly DateOnly Basis = new(2026, 11, 4);

    private static DateTime At(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    private static TaskItem Task(TaskItemStatus status = TaskItemStatus.Todo, string? due = null, string created = "2026-11-01T09:00:00",
        string? completed = null) => new()
    {
        Status = status,
        DueDate = due is null ? null : DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
        CreatedTime = At(created),
        CompletedTime = completed is null ? null : At(completed),
    };

    [Theory]
    [InlineData("2026-11-04T23:59:00", "2026-11-04", F.CompletionOnTime, 0)]
    [InlineData("2026-11-05T00:10:00", "2026-11-04", F.CompletionLate, 1)]
    public void Completion_AcrossMidnight(string completed, string due, long completion, int delay)
    {
        var t = Task(TaskItemStatus.Done, due, completed: completed);
        TaskReportValues.CompletionOf(t).Should().Be(completion);
        TaskReportValues.DelayDays(t, Basis).Should().Be(delay);
    }

    [Fact]
    public void Completion_NoDue_And_NotDone_And_DoneWithoutTime()
    {
        TaskReportValues.CompletionOf(Task(TaskItemStatus.Done, completed: "2026-11-02T10:00:00")).Should().Be(F.CompletionNoDue);
        TaskReportValues.CompletionOf(Task(TaskItemStatus.Todo, "2026-11-01")).Should().Be(F.CompletionNotDone);
        TaskReportValues.CompletionOf(Task(TaskItemStatus.Cancelled, "2026-11-01")).Should().Be(F.CompletionNotDone);
        TaskReportValues.CompletionOf(Task(TaskItemStatus.Done, "2026-11-01")).Should().BeNull();
    }

    [Fact]
    public void CompletionDays_SameDayIsZero_OpenIsNull()
    {
        TaskReportValues.CompletionDays(Task(TaskItemStatus.Done, created: "2026-11-02T09:00:00", completed: "2026-11-02T18:00:00")).Should().Be(0);
        TaskReportValues.CompletionDays(Task(TaskItemStatus.Done, created: "2026-11-01T09:00:00", completed: "2026-11-04T08:00:00")).Should().Be(3);
        TaskReportValues.CompletionDays(Task(TaskItemStatus.InProgress)).Should().BeNull();
    }

    [Fact]
    public void DelayDays_ByStatus()
    {
        TaskReportValues.DelayDays(Task(TaskItemStatus.Done, "2026-11-01", completed: "2026-11-03T10:00:00"), Basis).Should().Be(2);
        TaskReportValues.DelayDays(Task(TaskItemStatus.Done, "2026-11-03", completed: "2026-11-01T10:00:00"), Basis).Should().Be(0);
        TaskReportValues.DelayDays(Task(TaskItemStatus.Todo, "2026-11-01"), Basis).Should().Be(3);
        TaskReportValues.DelayDays(Task(TaskItemStatus.InProgress, "2026-11-10"), Basis).Should().Be(0);
        // B1：取消的任務延遲天數空白
        TaskReportValues.DelayDays(Task(TaskItemStatus.Cancelled, "2026-11-01"), Basis).Should().BeNull();
        TaskReportValues.DelayDays(Task(TaskItemStatus.Todo), Basis).Should().BeNull();
    }

    [Fact]
    public void IsOverdue_OnlyOpenBeforeBasis()
    {
        TaskReportValues.IsOverdue(Task(TaskItemStatus.Todo, "2026-11-03"), Basis).Should().BeTrue();
        TaskReportValues.IsOverdue(Task(TaskItemStatus.InProgress, "2026-11-03"), Basis).Should().BeTrue();
        TaskReportValues.IsOverdue(Task(TaskItemStatus.Todo, "2026-11-04"), Basis).Should().BeFalse();
        TaskReportValues.IsOverdue(Task(TaskItemStatus.Cancelled, "2026-11-01"), Basis).Should().BeFalse();
        TaskReportValues.IsOverdue(Task(TaskItemStatus.Done, "2026-11-01", completed: "2026-11-03T10:00:00"), Basis).Should().BeFalse();
        TaskReportValues.IsOverdue(Task(TaskItemStatus.Todo), Basis).Should().BeFalse();
    }

    [Fact]
    public void CompletionDays_And_DelayDays_DoneWithoutCompletedTime_AreNull()
    {
        var t = Task(TaskItemStatus.Done, "2026-11-01");
        TaskReportValues.CompletionDays(t).Should().BeNull();
        TaskReportValues.DelayDays(t, Basis).Should().BeNull();
    }

    [Fact]
    public void SourceOf_ThreeKinds()
    {
        TaskReportValues.SourceOf(new TaskItem()).Should().Be(F.SourcePersonal);
        TaskReportValues.SourceOf(new TaskItem { ModuleId = 3, RecordId = 9 }).Should().Be(F.SourceRecord);
        TaskReportValues.SourceOf(new TaskItem { ModuleId = 3, RecordId = 9, RuleId = 2 }).Should().Be(F.SourceRule);
    }
}
