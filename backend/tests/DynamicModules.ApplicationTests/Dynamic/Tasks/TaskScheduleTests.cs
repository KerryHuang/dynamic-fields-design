using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>甘特圖改日期 POST /tasks/{id}/schedule（看板設計 §11.5.2.6，G26–G33）。</summary>
public class TaskScheduleTests : TaskBoardTestBase
{
    private static DateOnly D(int day) => new(2026, 11, day);

    private async Task<ServiceResult> Schedule(string user, long id, DateOnly? start, DateOnly? due, string? version = null) =>
        await SendAs(user, new ScheduleTaskRequest(id, start, due, version ?? TaskVersion.Of(await Load(id))));

    private static TaskCardResponse Card(ServiceResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        return ((TaskScheduleResponse)r.Data!).Card;
    }

    private Task<List<TaskComment>> SystemEntries(long id) =>
        Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == id && c.Kind == TaskCommentKind.System).OrderBy(c => c.Id).ToListAsync();

    [Fact]
    public async Task G26_Success_NewDatesAndVersion_TwoSystemEntries()
    {
        var task = await SeedTask("佈置展場", createdBy: "sales01", assignee: "sales01", due: D(12), start: D(6));
        var version = TaskVersion.Of(await Load(task.Id));

        var card = Card(await Schedule("sales01", task.Id, D(21), D(28), version));

        (card.StartDate, card.DueDate).Should().Be((D(21), D(28)));
        card.Version.Should().NotBe(version);
        card.Version.Should().Be(TaskVersion.Of(await Load(task.Id)));
        var entries = await SystemEntries(task.Id);
        entries.Select(c => (c.SystemEvent, c.AuthorUserId)).Should().Equal(
            (TaskSystemEvents.DueChanged, "sales01"), (TaskSystemEvents.StartChanged, "sales01"));
        J(entries[1].SystemData!).GetRawText().Should().Be("""{"from":"2026-11-06","to":"2026-11-21"}""");
    }

    [Fact]
    public async Task G27_RuleTask_ShiftSetsDueOverridden_StartOnlyDoesNot()
    {
        var shifted = await SeedTask("規則任務甲", createdBy: null, assignee: "sales01", ruleId: 7, due: D(20), start: D(15));
        var startOnly = await SeedTask("規則任務乙", createdBy: null, assignee: "sales01", ruleId: 7, due: D(20), start: D(15));

        Card(await Schedule("sales01", shifted.Id, D(17), D(22)));
        Card(await Schedule("sales01", startOnly.Id, D(10), D(20)));

        (await Load(shifted.Id)).DueOverridden.Should().BeTrue();
        var after = await Load(startOnly.Id);
        after.DueOverridden.Should().BeFalse();
        after.StartDate.Should().Be(D(10));
        (await SystemEntries(startOnly.Id)).Select(c => c.SystemEvent).Should().Equal(TaskSystemEvents.StartChanged);
    }

    [Fact]
    public async Task G28_StaleVersion_409_MissingVersion_400()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(12));
        var stale = TaskVersion.Of(await Load(task.Id));
        Card(await Schedule("sales01", task.Id, null, D(13)));

        var r = await Schedule("sales01", task.Id, D(1), D(20), stale);

        r.IsConflict.Should().BeTrue();
        r.Code.Should().Be("stale");
        var after = await Load(task.Id);
        (after.StartDate, after.DueDate).Should().Be((null, D(13)));
        var missing = await SendAs("sales01", new ScheduleTaskRequest(task.Id, D(1), D(20), null));
        missing.Errors.Should().ContainKey("expectedVersion");
        (await SendAs("sales01", new ScheduleTaskRequest(task.Id, D(1), D(20), ""))).Errors.Should().ContainKey("expectedVersion");
    }

    /// <summary>
    /// 兩個請求都讀到版本 V（同 <see cref="TaskMoveTests"/> 的模擬）：另一個請求在讀完之後已把版本推進，
    /// 記憶體比較通過但認領失敗 → 409，沒有系統記錄、日期不變。真正的並行 HTTP 見 TaskBoardApiTests。
    /// </summary>
    [Fact]
    public async Task G29_VersionChangedAfterRead_409_NoDuplicateEntries()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(20));
        var version = TaskVersion.Of(await Load(task.Id));
        await Db.Context.Set<TaskItem>().SingleAsync(t => t.Id == task.Id);
        await Db.Context.Set<TaskItem>().Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ModifiedTime, new DateTime(2026, 11, 4, 9, 30, 0)));

        As("sales01");
        var r = await Send(new ScheduleTaskRequest(task.Id, D(18), D(25), version));
        As("admin");

        r.IsConflict.Should().BeTrue();
        r.Code.Should().Be("stale");
        Db.Context.ChangeTracker.Clear();
        (await SystemEntries(task.Id)).Should().BeEmpty();
        var after = await Load(task.Id);
        (after.StartDate, after.DueDate).Should().Be((null, D(20)));
    }

    [Fact]
    public async Task G30_StartAfterDue_400_NothingWritten()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(12));

        var r = await Schedule("sales01", task.Id, D(21), D(20));

        r.Errors.Should().ContainKey("startDate");
        r.Errors["startDate"].Should().Equal("開始日不能晚於到期日");
        (await Load(task.Id)).DueDate.Should().Be(D(12));
        (await SystemEntries(task.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task G31_Supervisor403_Outsider404()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01", due: D(12));

        var supervisor = await Schedule("manager01", task.Id, D(5), D(6));
        var outsider = await Schedule("keeper01", task.Id, D(5), D(6));

        supervisor.IsForbidden.Should().BeTrue();
        supervisor.Message.Should().Be(TaskMessages.SupervisorOnlyAssignee);
        outsider.IsNotFound.Should().BeTrue();
        (await Load(task.Id)).DueDate.Should().Be(D(12));
    }

    [Fact]
    public async Task G31_Frozen_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask("掛記錄", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec, due: D(12));
        await Db.Context.Set<Module>().Where(m => m.Key == t.Key).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var r = await Schedule("sales01", task.Id, D(5), D(6));

        r.IsConflict.Should().BeTrue();
        r.Code.Should().BeNull();
        (await Load(task.Id)).DueDate.Should().Be(D(12));
    }

    [Fact]
    public async Task G31_SubtaskAssignee_And_ParentEditor_200()
    {
        var parent = await SeedTask("籌備展覽", createdBy: "sales01", assignee: "sales01");
        var child = await SeedTask("訂攤位", createdBy: "sales02", assignee: "sales03", due: D(12));
        await Db.Context.Set<TaskItem>().Where(x => x.Id == child.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ParentTaskId, parent.Id));

        Card(await Schedule("sales03", child.Id, D(9), D(13))).DueDate.Should().Be(D(13));
        Card(await Schedule("sales01", child.Id, D(10), D(14))).StartDate.Should().Be(D(10));
    }

    [Fact]
    public async Task G32_NoChange_200_NoEntry_SameVersion()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(12), start: D(6));
        var version = TaskVersion.Of(await Load(task.Id));

        var card = Card(await Schedule("sales01", task.Id, D(6), D(12), version));

        card.Version.Should().Be(version);
        TaskVersion.Of(await Load(task.Id)).Should().Be(version);
        (await SystemEntries(task.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task G33_ClearBoth_200()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(12), start: D(6));

        var card = Card(await Schedule("sales01", task.Id, null, null));

        (card.StartDate, card.DueDate).Should().Be((null, null));
        var after = await Load(task.Id);
        (after.StartDate, after.DueDate).Should().Be((null, null));
        (await SystemEntries(task.Id)).Select(c => c.SystemEvent).Should().Equal(TaskSystemEvents.DueChanged, TaskSystemEvents.StartChanged);
    }
}
