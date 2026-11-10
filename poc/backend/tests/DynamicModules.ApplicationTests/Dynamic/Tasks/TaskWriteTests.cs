using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

public class TaskWriteTests : TaskTestBase
{
    private async Task<TaskDetailResponse> MustCreate(string title = "準備資料", string? assignee = null, DateOnly? due = null)
    {
        var r = await Send(new CreateTaskRequest(new TaskInput(title, null, assignee, due)));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (TaskDetailResponse)r.Data!;
    }

    private Task<ServiceResult> Patch(long id, string json) => Send(new UpdateTaskRequest(id, J(json)));

    private async Task<List<TaskComment>> Entries(long taskId)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId).OrderBy(c => c.Id).ToListAsync();
    }

    [Fact]
    public async Task Create_WritesCreatedEntry_CreatorAndAssigneeParticipants()
    {
        var created = await MustCreate(assignee: "sales01");

        var entries = await Entries(created.Id);
        entries.Should().ContainSingle();
        entries[0].Kind.Should().Be(TaskCommentKind.System);
        entries[0].SystemEvent.Should().Be(TaskSystemEvents.Created);
        entries[0].AuthorUserId.Should().Be("admin");
        (await Load(created.Id)).Participants.Select(p => (p.UserId, p.Reason)).Should().BeEquivalentTo(
            [("admin", TaskParticipantReason.Creator), ("sales01", TaskParticipantReason.Assignee)]);
        created.Status.Should().Be(TaskItemStatus.Todo);
        created.AssigneeName.Should().Be("業務一");
        created.CanDelete.Should().BeTrue();
        created.Record.Should().BeNull();
    }

    [Fact]
    public async Task ChangeAssignee_KeepsOldAsParticipant_WritesAssigneeChanged()
    {
        var created = await MustCreate(assignee: "sales01");

        (await Patch(created.Id, """{"assigneeUserId":"sales02"}""")).IsSuccess.Should().BeTrue();

        var entry = (await Entries(created.Id)).Single(c => c.SystemEvent == TaskSystemEvents.AssigneeChanged);
        J(entry.SystemData!).GetRawText().Should().Be("""{"from":"sales01","to":"sales02"}""");
        (await Load(created.Id)).Participants.Select(p => p.UserId).Should().Contain(["sales01", "sales02"]);
    }

    [Fact]
    public async Task StatusDone_SetsCompletedTime_LeavingDoneClears()
    {
        var created = await MustCreate();

        (await Patch(created.Id, """{"status":"done"}""")).IsSuccess.Should().BeTrue();
        (await Load(created.Id)).CompletedTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
        (await Patch(created.Id, """{"status":"todo"}""")).IsSuccess.Should().BeTrue();

        var task = await Load(created.Id);
        task.CompletedTime.Should().BeNull();
        task.Status.Should().Be(TaskItemStatus.Todo);
        var entry = (await Entries(created.Id)).Last();
        entry.SystemEvent.Should().Be(TaskSystemEvents.StatusChanged);
        J(entry.SystemData!).GetRawText().Should().Be("""{"from":"done","to":"todo"}""");
    }

    [Fact]
    public async Task Patch_NullClearsDueDate_AbsentLeavesIt()
    {
        var created = await MustCreate(assignee: "sales01", due: new DateOnly(2026, 11, 10));

        (await Patch(created.Id, """{"title":"x"}""")).IsSuccess.Should().BeTrue();
        var afterTitle = await Load(created.Id);
        afterTitle.Title.Should().Be("x");
        afterTitle.DueDate.Should().Be(new DateOnly(2026, 11, 10));
        afterTitle.AssigneeUserId.Should().Be("sales01");

        (await Patch(created.Id, """{"dueDate":null}""")).IsSuccess.Should().BeTrue();
        var afterDue = await Load(created.Id);
        afterDue.DueDate.Should().BeNull();
        afterDue.AssigneeUserId.Should().Be("sales01");

        (await Patch(created.Id, """{"assigneeUserId":null}""")).IsSuccess.Should().BeTrue();
        (await Load(created.Id)).AssigneeUserId.Should().BeNull();
    }

    [Fact]
    public async Task Patch_BadDateOrStatus_400()
    {
        var created = await MustCreate();

        Err(await Patch(created.Id, """{"dueDate":"2026/11/10"}"""), "dueDate").Should().Be(TaskMessages.BadDate);
        Err(await Patch(created.Id, """{"status":"Done"}"""), "status").Should().Be(TaskMessages.BadStatus);
    }

    [Fact]
    public async Task AssignToInactiveUser_400()
    {
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "floor02").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var created = await MustCreate();

        var r = await Patch(created.Id, """{"assigneeUserId":"floor02"}""");

        Err(r, "assigneeUserId").Should().Be("負責人必須是啟用中的使用者");
        Err(await Send(new CreateTaskRequest(new TaskInput("x", null, "nobody", null))), "assigneeUserId")
            .Should().Be(TaskMessages.AssigneeInvalid);
    }

    [Fact]
    public async Task Title_BlankOr201Chars_400()
    {
        Err(await Send(new CreateTaskRequest(new TaskInput("  ", null, null, null))), "title").Should().Be("標題必填");
        Err(await Send(new CreateTaskRequest(new TaskInput(new string('甲', 201), null, null, null))), "title").Should().Be("標題不能超過 200 字");
        var created = await MustCreate(new string('甲', 200));
        Err(await Patch(created.Id, """{"title":""}"""), "title").Should().Be(TaskMessages.TitleRequired);
        Err(await Patch(created.Id, """{"title":null}"""), "title").Should().Be(TaskMessages.TitleRequired);
    }

    [Fact]
    public async Task RuleTask_HumanEdit_SetsOverriddenFlags()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var seeded = await SeedTask(createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: 1);

        (await Patch(seeded.Id, """{"dueDate":"2026-11-20"}""")).IsSuccess.Should().BeTrue();

        var task = await Load(seeded.Id);
        task.DueOverridden.Should().BeTrue();
        task.AssigneeOverridden.Should().BeFalse();
    }

    [Fact]
    public async Task RuleTask_Delete_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var seeded = await SeedTask(createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: 1);

        var r = await Send(new DeleteTaskRequest(seeded.Id));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(TaskMessages.RuleTaskNoDelete);
    }

    [Fact]
    public async Task Creator_Or_Admin_CanDelete_AssigneeCannot_403()
    {
        As("sales01");
        var mine = await MustCreate(assignee: "sales02");
        var other = await MustCreate(assignee: "sales02");

        As("sales02");
        (await Send(new DeleteTaskRequest(mine.Id))).IsForbidden.Should().BeTrue();
        As("sales01");
        (await Send(new DeleteTaskRequest(mine.Id))).IsSuccess.Should().BeTrue();
        As("admin");
        (await Send(new DeleteTaskRequest(other.Id))).IsSuccess.Should().BeTrue();

        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskItem>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<TaskComment>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<TaskParticipant>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InvisibleTask_404_ReaderPatch_403()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var seeded = await SeedTask(createdBy: "admin", target: t, recordId: rec);

        As("floor01");
        var hidden = await Patch(seeded.Id, """{"title":"x"}""");
        hidden.IsNotFound.Should().BeTrue();
        hidden.Message.Should().Be(TaskMessages.NotFound);
        As("manager01");
        (await Patch(seeded.Id, """{"title":"x"}""")).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task ClosedModule_Patch_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var seeded = await SeedTask(createdBy: "admin", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Id == t.ModuleId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var r = await Patch(seeded.Id, """{"title":"x"}""");

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("應用程式已關閉，資料唯讀");
    }

    [Fact]
    public async Task CreateRecordTask_SnapshotsTitle_OutOfScope404()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01", "案件乙");

        As("sales02");
        var hidden = await Send(new CreateRecordTaskRequest(rec, new TaskInput("x", null, null, null)));
        hidden.IsNotFound.Should().BeTrue();
        As("sales01");
        var r = await Send(new CreateRecordTaskRequest(rec, new TaskInput("聯絡窗口", null, "sales01", null)));

        r.IsCreated.Should().BeTrue(r.Message);
        var detail = (TaskDetailResponse)r.Data!;
        detail.Record.Should().Be(new TaskRecordRef(true, t.Key, rec, "案件乙", false));
        var task = await Load(detail.Id);
        task.RecordTitleSnapshot.Should().Be("案件乙");
        task.ModuleId.Should().Be(t.ModuleId);
    }
}
