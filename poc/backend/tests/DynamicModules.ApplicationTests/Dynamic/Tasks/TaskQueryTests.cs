using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>FakeTime 的今天是 2026-11-04（週三），本週日是 11-08。</summary>
public class TaskQueryTests : TaskTestBase
{
    private async Task<List<DateOnly?>> Mine(string user, string tab, string filter)
    {
        As(user);
        var r = await Send(new GetMyTasksRequest(tab, filter, 1, 20));
        As("admin");
        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key)));
        return ((PagedResponse<TaskSummaryResponse>)r.Data!).Items.Select(i => i.DueDate).ToList();
    }

    private static DateOnly D(int month, int day) => new(month == 1 ? 2027 : 2026, month, day);

    [Fact]
    public async Task MyTasks_TabsAndFilters()
    {
        foreach (var day in new[] { 9, 3, 8, 4 })
            await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, day));
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 4), status: TaskItemStatus.Done);
        await SeedTask(createdBy: "sales01", assignee: "sales02", due: D(11, 20));
        await SeedTask(createdBy: "admin", assignee: "sales02", due: null, mentioned: ["sales01"]);

        (await Mine("sales01", "assigned", "open")).Should().Equal(D(11, 3), D(11, 4), D(11, 8), D(11, 9));
        (await Mine("sales01", "assigned", "overdue")).Should().Equal(D(11, 3));
        (await Mine("sales01", "assigned", "thisWeek")).Should().Equal(D(11, 4), D(11, 8));
        (await Mine("sales01", "assigned", "all")).Should().HaveCount(5);
        (await Mine("sales01", "created", "open")).Should().Equal(D(11, 20));
        (await Mine("sales01", "mentioned", "open")).Should().Equal([null]);
    }

    [Fact]
    public async Task MyTasks_NullDueLast_UnknownTabOrFilter400()
    {
        await SeedTask(createdBy: "admin", assignee: "sales01", due: null);
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 30));

        (await Mine("sales01", "assigned", "open")).Should().Equal(D(11, 30), null);
        var bad = await Send(new GetMyTasksRequest("mine", "soon", 1, 20));
        bad.Errors.Keys.Should().BeEquivalentTo(["tab", "filter"]);
    }

    [Fact]
    public async Task TaskSummary_CountsDueTodayAndOverdue()
    {
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 3));
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 4));
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 5));
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 4), status: TaskItemStatus.Done);
        await SeedTask(createdBy: "admin", assignee: "sales01", due: D(11, 2), status: TaskItemStatus.Cancelled);

        As("sales01");
        var r = await Send(new GetTaskSummaryRequest());

        r.Data.Should().Be(new TaskSummaryCounts(1, 1));
    }

    [Fact]
    public async Task RecordTasks_RequiresRecordRead_404Otherwise()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        await SeedTask(createdBy: "admin", assignee: "sales01", target: t, recordId: rec, due: D(11, 9));
        await SeedTask(createdBy: "admin", target: t, recordId: rec, due: D(11, 5), ruleId: 7);

        As("sales02");
        var hidden = await Send(new GetRecordTasksRequest(rec));
        hidden.IsNotFound.Should().BeTrue();
        As("sales01");
        var r = await Send(new GetRecordTasksRequest(rec));

        var body = (RecordTasksResponse)r.Data!;
        body.CanAdd.Should().BeTrue();
        body.ManualRules.Should().BeEmpty();
        body.Items.Select(i => i.DueDate).Should().Equal(D(11, 5), D(11, 9));
        body.Items[0].FromRule.Should().BeTrue();
        body.Items[1].AssigneeName.Should().Be("業務一");
        body.Items[1].Record.Should().Be(new TaskRecordRef(true, t.Key, rec, "案件甲", false));
    }

    [Fact]
    public async Task GetTask_InvisibleAndMissing_SameNotFound()
    {
        var seeded = await SeedTask(createdBy: "sales01");

        As("floor01");
        var hidden = await Send(new GetTaskRequest(seeded.Id));
        var missing = await Send(new GetTaskRequest(seeded.Id + 1000));

        hidden.IsNotFound.Should().BeTrue();
        hidden.Message.Should().Be(TaskMessages.NotFound);
        missing.Message.Should().Be(hidden.Message);
        As("sales01");
        ((TaskDetailResponse)(await Send(new GetTaskRequest(seeded.Id))).Data!).CreatedByName.Should().Be("業務一");
    }

    [Fact]
    public async Task RuleTask_DeletedRule_FlaggedRuleDeleted()
    {
        var seeded = await SeedTask(createdBy: null, assignee: "sales01", ruleId: 99);

        As("sales01");
        var detail = (TaskDetailResponse)(await Send(new GetTaskRequest(seeded.Id))).Data!;

        detail.RuleKey.Should().BeNull();
        detail.RuleDeleted.Should().BeTrue();
        detail.CanDelete.Should().BeFalse();
    }
}
