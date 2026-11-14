using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板移動（看板設計 §5.2–§5.6）：伺服器算日期、權限、版本、覆寫旗標、移出範圍與復原。</summary>
public class TaskMoveTests : TaskBoardTestBase
{
    private static DateOnly D(int day) => new(2026, 11, day);

    private async Task<ServiceResult> Move(string user, TaskItem task, string groupBy, string to, DateOnly? due = null, string? version = null) =>
        await SendAs(user, new MoveTaskRequest(task.Id, groupBy, to, due, version ?? TaskVersion.Of(await Load(task.Id))));

    private static TaskCardResponse Card(ServiceResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        var m = (TaskMoveResponse)r.Data!;
        m.Visible.Should().BeTrue();
        return m.Card!;
    }

    [Fact]
    public async Task Status_ToDone_SetsCompleted_FromDone_Clears()
    {
        var task = await SeedTask("回覆樣品問題", createdBy: "sales01", assignee: "sales01");

        var done = Card(await Move("sales01", task, "status", "done"));
        done.Status.Should().Be(TaskItemStatus.Done);
        done.CompletedTime.Should().NotBeNull();
        (await Load(task.Id)).CompletedTime.Should().NotBeNull();

        var back = Card(await Move("sales01", task, "status", "inProgress"));
        back.Status.Should().Be(TaskItemStatus.InProgress);
        (await Load(task.Id)).CompletedTime.Should().BeNull();
        (await Move("sales01", task, "status", "archived")).Errors.Should().ContainKey("toColumn");
        (await Move("sales01", task, "kanban", "done")).Errors.Should().ContainKey("groupBy");
    }

    [Fact]
    public async Task Status_Supervisor_403()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var r = await Move("manager01", task, "status", "done");
        var due = await Move("manager01", task, "dueBucket", "today");

        r.IsForbidden.Should().BeTrue();
        due.IsForbidden.Should().BeTrue();
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.Todo);
    }

    [Fact]
    public async Task Assignee_Supervisor_Allowed()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var card = Card(await Move("manager01", task, "assignee", "user:sales02"));

        card.AssigneeUserId.Should().Be("sales02");
        card.AssigneeName.Should().Be("業務二");
        card.CanEdit.Should().BeFalse();
        card.CanReassign.Should().BeTrue();
        (await Load(task.Id)).Participants.Should().NotContain(p => p.UserId == "manager01");
        Card(await Move("sales01", task, "assignee", "unassigned")).Should().NotBeNull();   // 建立者照樣能改
        (await Load(task.Id)).AssigneeUserId.Should().BeNull();
    }

    [Fact]
    public async Task Assignee_ToInactive_400()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "sales03").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        (await Move("sales01", task, "assignee", "user:sales03")).Errors.Should().ContainKey("toColumn");
        (await Move("sales01", task, "assignee", "user:ghost")).Errors.Should().ContainKey("toColumn");
        (await Move("sales01", task, "assignee", "sales02")).Errors.Should().ContainKey("toColumn");
        (await Load(task.Id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Assignee_RuleTask_SetsAssigneeOverridden()
    {
        var task = await SeedTask("規則任務", createdBy: null, assignee: "sales01", ruleId: 7, mentioned: ["admin"]);

        Card(await Move("admin", task, "assignee", "user:sales02"));

        var after = await Load(task.Id);
        after.AssigneeOverridden.Should().BeTrue();
        after.DueOverridden.Should().BeFalse();
    }

    [Fact]
    public async Task Due_Today_ThisWeek_Later_None_Values()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(20));

        Card(await Move("sales01", task, "dueBucket", "today")).DueDate.Should().Be(Today);
        Card(await Move("sales01", task, "dueBucket", "thisWeek")).DueDate.Should().Be(D(8));
        Card(await Move("sales01", task, "dueBucket", "later", D(12))).DueDate.Should().Be(D(12));
        Card(await Move("sales01", task, "dueBucket", "none")).DueDate.Should().BeNull();
        (await Load(task.Id)).DueDate.Should().BeNull();
    }

    [Fact]
    public async Task Due_ToOverdue_400()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(20));

        var r = await Move("sales01", task, "dueBucket", "overdue");

        r.Errors.Should().ContainKey("toColumn");
        (await Load(task.Id)).DueDate.Should().Be(D(20));
    }

    [Fact]
    public async Task Move_ToThisWeek_WhenTodayIsWeekEnd_400()
    {
        BusinessClock.Now = new DateTime(2026, 11, 8, 9, 0, 0);   // 週日＝本週最後一天
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(20));

        var r = await Move("sales01", task, "dueBucket", "thisWeek");

        r.Errors.Should().ContainKey("toColumn");
        (await Load(task.Id)).DueDate.Should().Be(D(20));
        var b = await Board("sales01", """{"version":1,"groupBy":"dueBucket","scope":"assigned"}""");
        Col(b, "thisWeek").Droppable.Should().BeFalse();
    }

    [Fact]
    public async Task Due_LaterNotAfterWeekEnd_400()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(5));

        (await Move("sales01", task, "dueBucket", "later", D(8))).Errors.Should().ContainKey("dueDate");
        (await Move("sales01", task, "dueBucket", "later")).Errors.Should().ContainKey("dueDate");
        (await Load(task.Id)).DueDate.Should().Be(D(5));
    }

    [Fact]
    public async Task Due_ClearRuleTask_SetsDueOverridden_RuleNoLongerSyncs()
    {
        var t = await BuildTarget();
        var rule = await SendAs("admin", new CreateTaskRuleRequest(t.Key, new TaskRuleInput("followup", Text("跟進"), true, TaskRuleRepeat.Once,
            new TaskRuleTriggerInput("onCreate", null, null), null,
            [new TaskRuleTaskInput("t1", Text("跟進客戶"), null, t.UserFieldId, t.DateFieldId, 0, null)])));
        rule.IsCreated.Should().BeTrue(rule.Message + string.Join(";", rule.Errors.SelectMany(e => e.Value)));
        var rec = await NewRecordWith(t, "sales01", "2026-11-20");
        var task = await Db.Context.Set<TaskItem>().AsNoTracking().Include(x => x.Participants).SingleAsync(x => x.RecordId == rec);
        task.DueDate.Should().Be(D(20));

        Card(await Move("sales01", task, "dueBucket", "none")).DueDate.Should().BeNull();
        (await Load(task.Id)).DueOverridden.Should().BeTrue();

        await UpdateRecordDate(t, rec, "2026-11-25");
        (await Load(task.Id)).DueDate.Should().BeNull();
    }

    [Fact]
    public async Task Move_StaleVersion_409_NothingWritten()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01");
        var stale = TaskVersion.Of(await Load(task.Id));
        Card(await Move("sales01", task, "status", "inProgress"));

        var r = await Move("sales01", task, "status", "done", version: stale);

        r.IsConflict.Should().BeTrue();
        r.Code.Should().Be("stale");
        r.Message.Should().Be(TaskMessages.Stale);
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.InProgress);
        (await SendAs("sales01", new MoveTaskRequest(task.Id, "status", "done", null, null))).Errors.Should().ContainKey("expectedVersion");
    }

    [Fact]
    public async Task Move_Frozen_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask("掛記錄", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Key == t.Key).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var r = await Move("sales01", task, "status", "done");

        r.IsConflict.Should().BeTrue();
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.Todo);
        var b = await Board("sales01");
        Col(b, "todo").Cards.Single().Frozen.Should().BeTrue();
    }

    [Fact]
    public async Task Move_SameColumn_NoChange()
    {
        var task = await SeedTask("準備報價", createdBy: "sales01", assignee: "sales01", due: D(6));
        var version = TaskVersion.Of(await Load(task.Id));

        Card(await Move("sales01", task, "dueBucket", "thisWeek")).DueDate.Should().Be(D(6));
        Card(await Move("sales01", task, "status", "todo"));
        Card(await Move("sales01", task, "assignee", "user:sales01"));

        var after = await Load(task.Id);
        TaskVersion.Of(after).Should().Be(version);
        (await Db.Context.Set<TaskComment>().CountAsync(c => c.TaskId == task.Id && c.Kind == TaskCommentKind.System)).Should().Be(0);
    }

    [Fact]
    public async Task Move_ToNonSubordinate_VisibleFalse_Undo()
    {
        var task = await SeedTask("盤點備品倉", createdBy: "keeper01", assignee: "keeper01");

        var r = await Move("ops01", task, "assignee", "user:sales01");

        r.IsSuccess.Should().BeTrue(r.Message);
        var m = (TaskMoveResponse)r.Data!;
        m.Visible.Should().BeFalse();
        m.Card.Should().BeNull();
        m.UndoToken.Should().NotBeNullOrEmpty();
        var undo = await SendAs("ops01", new UndoReassignRequest(task.Id, m.UndoToken!));
        undo.IsSuccess.Should().BeTrue(undo.Message);
        (await Load(task.Id)).AssigneeUserId.Should().Be("keeper01");
    }

    [Fact]
    public async Task Move_WritesSameSystemEventAsPatch()
    {
        var viaMove = await SeedTask("看板", createdBy: "sales01", assignee: "sales01", due: D(20));
        var viaPatch = await SeedTask("對話框", createdBy: "sales01", assignee: "sales01", due: D(20));

        Card(await Move("sales01", viaMove, "assignee", "user:sales02"));
        Card(await Move("sales01", viaMove, "dueBucket", "today"));
        Card(await Move("sales01", viaMove, "status", "done"));
        (await SendAs("sales01", new UpdateTaskRequest(viaPatch.Id, J("""{"assigneeUserId":"sales02"}""")))).IsSuccess.Should().BeTrue();
        (await SendAs("sales01", new UpdateTaskRequest(viaPatch.Id, J("""{"dueDate":"2026-11-04"}""")))).IsSuccess.Should().BeTrue();
        (await SendAs("sales01", new UpdateTaskRequest(viaPatch.Id, J("""{"status":"done"}""")))).IsSuccess.Should().BeTrue();

        async Task<List<(string?, string?, string?)>> Events(long id) =>
            (await Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == id && c.Kind == TaskCommentKind.System)
                .OrderBy(c => c.Id).ToListAsync()).Select(c => (c.SystemEvent, c.SystemData, c.AuthorUserId)).ToList();

        (await Events(viaMove.Id)).Should().Equal(await Events(viaPatch.Id));
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == "sales02" && n.SourceId == viaMove.Id))
            .Should().Be(await Db.Context.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == "sales02" && n.SourceId == viaPatch.Id));
    }

    private async Task<long> NewRecordWith(Target t, string owner, string date)
    {
        var r = await SendAs("admin", new Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(t.Key,
            new Dictionary<string, System.Text.Json.JsonElement?>
            {
                [$"f_{t.TitleFieldId}"] = J("\"案件甲\""), [$"f_{t.UserFieldId}"] = J($"\"{owner}\""), [$"f_{t.DateFieldId}"] = J($"\"{date}\"")
            }));
        r.IsCreated.Should().BeTrue(r.Message);
        return ((DynamicRecordResponse)r.Data!).Id;
    }

    private async Task UpdateRecordDate(Target t, long id, string date)
    {
        var rowVersion = (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == id)).RowVersion;
        var r = await SendAs("admin", new UpdateRecordRequest(id,
            new Dictionary<string, System.Text.Json.JsonElement?> { [$"f_{t.DateFieldId}"] = J($"\"{date}\"") }, rowVersion));
        r.IsSuccess.Should().BeTrue(r.Message);
    }
}
