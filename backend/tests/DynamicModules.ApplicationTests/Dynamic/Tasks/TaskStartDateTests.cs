using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務開始日（看板設計 §11.5.2.1–§11.5.2.4，驗收 G1–G8、G11）：建立、PATCH 驗證、到期日改早時開始日跟著移、DTO。</summary>
public class TaskStartDateTests : TaskTestBase
{
    private static DateOnly D(int day) => new(2026, 11, day);

    private Task<ServiceResult> Create(DateOnly? start, DateOnly? due) =>
        Send(new CreateTaskRequest(new TaskInput("準備展場", null, null, due, start)));

    private async Task<TaskDetailResponse> MustCreate(DateOnly? start, DateOnly? due)
    {
        var r = await Create(start, due);
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (TaskDetailResponse)r.Data!;
    }

    private Task<ServiceResult> Patch(long id, string json) => Send(new UpdateTaskRequest(id, J(json)));

    private async Task<List<TaskComment>> Entries(long taskId)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId && c.Kind == TaskCommentKind.System)
            .OrderBy(c => c.Id).ToListAsync();
    }

    private Task<int> TaskCount() => Db.Context.Set<TaskItem>().AsNoTracking().CountAsync();

    [Fact]
    public async Task G1_Create_WithStartDate_OnlyCreatedEntry()
    {
        As("sales01");
        var created = await MustCreate(D(20), D(25));

        created.StartDate.Should().Be(D(20));
        created.DueDate.Should().Be(D(25));
        (await Load(created.Id)).StartDate.Should().Be(D(20));
        (await Entries(created.Id)).Select(c => c.SystemEvent).Should().Equal(TaskSystemEvents.Created);
    }

    [Fact]
    public async Task G2_Create_StartAfterDue_400_NothingCreated_AllThreeApis()
    {
        var before = await TaskCount();
        Err(await Create(D(26), D(25)), "startDate").Should().Be("開始日不能晚於到期日");

        var t = await BuildTarget();
        var rec = await NewRecord(t, "admin");
        Err(await Send(new CreateRecordTaskRequest(rec, new TaskInput("x", null, null, D(25), D(26)))), "startDate")
            .Should().Be("開始日不能晚於到期日");

        var parent = await MustCreate(null, null);
        Err(await Send(new CreateSubtaskRequest(parent.Id, new TaskInput("x", null, null, D(25), D(26)))), "startDate")
            .Should().Be("開始日不能晚於到期日");

        (await TaskCount()).Should().Be(before + 1, "只有用來當父任務的那一筆");
        (await Entries(parent.Id)).Select(c => c.SystemEvent).Should().NotContain(TaskSystemEvents.SubtaskAdded);
    }

    [Fact]
    public async Task G2_RecordAndSubtask_AcceptStartDate()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "admin");
        var r = await Send(new CreateRecordTaskRequest(rec, new TaskInput("x", null, null, D(25), D(20))));
        ((TaskDetailResponse)r.Data!).StartDate.Should().Be(D(20));

        var s = await Send(new CreateSubtaskRequest(((TaskDetailResponse)r.Data!).Id, new TaskInput("y", null, null, D(25), D(21))));
        s.IsCreated.Should().BeTrue(s.Message);
        ((TaskDetailResponse)s.Data!).StartDate.Should().Be(D(21));
    }

    [Fact]
    public async Task G3_Create_StartOnly_201()
    {
        var created = await MustCreate(D(20), null);

        created.StartDate.Should().Be(D(20));
        created.DueDate.Should().BeNull();
    }

    [Fact]
    public async Task G4_Patch_BadFormat_400()
    {
        var created = await MustCreate(null, null);

        Err(await Patch(created.Id, """{"startDate":"2026/11/20"}"""), "startDate").Should().Be(TaskMessages.BadDate);
        Err(await Patch(created.Id, """{"startDate":20}"""), "startDate").Should().Be(TaskMessages.BadDate);
    }

    [Fact]
    public async Task Patch_StartDate_WritesStartChanged_NullClears_AbsentLeaves()
    {
        As("sales01");
        var created = await MustCreate(null, D(25));

        (await Patch(created.Id, """{"startDate":"2026-11-20"}""")).IsSuccess.Should().BeTrue();
        (await Patch(created.Id, """{"title":"改標題"}""")).IsSuccess.Should().BeTrue();
        (await Load(created.Id)).StartDate.Should().Be(D(20));
        (await Patch(created.Id, """{"startDate":null}""")).IsSuccess.Should().BeTrue();

        (await Load(created.Id)).StartDate.Should().BeNull();
        var starts = (await Entries(created.Id)).Where(c => c.SystemEvent == TaskSystemEvents.StartChanged).ToList();
        starts.Select(c => J(c.SystemData!).GetRawText()).Should().Equal(
            """{"from":null,"to":"2026-11-20"}""", """{"from":"2026-11-20","to":null}""");
        starts.Should().OnlyContain(c => c.AuthorUserId == "sales01");
    }

    [Fact]
    public async Task G5_Patch_StartOnly_AfterDue_400_Unchanged()
    {
        var created = await MustCreate(D(20), D(25));
        var before = (await Entries(created.Id)).Count;

        Err(await Patch(created.Id, """{"startDate":"2026-11-27"}"""), "startDate").Should().Be("開始日不能晚於到期日");

        var after = await Load(created.Id);
        after.StartDate.Should().Be(D(20));
        after.DueDate.Should().Be(D(25));
        (await Entries(created.Id)).Should().HaveCount(before);
    }

    [Fact]
    public async Task G6_Patch_BothConflicting_400_Unchanged()
    {
        var created = await MustCreate(D(20), D(25));

        Err(await Patch(created.Id, """{"startDate":"2026-11-27","dueDate":"2026-11-26","title":"新標題"}"""), "startDate")
            .Should().Be("開始日不能晚於到期日");

        var after = await Load(created.Id);
        (after.StartDate, after.DueDate, after.Title).Should().Be((D(20), D(25), "準備展場"));
    }

    [Fact]
    public async Task Patch_BothValid_WritesDueChangedAndStartChanged()
    {
        var created = await MustCreate(D(20), D(25));

        (await Patch(created.Id, """{"startDate":"2026-11-10","dueDate":"2026-11-12"}""")).IsSuccess.Should().BeTrue();

        var after = await Load(created.Id);
        (after.StartDate, after.DueDate).Should().Be((D(10), D(12)));
        (await Entries(created.Id)).Select(c => c.SystemEvent).Should().Equal(
            TaskSystemEvents.Created, TaskSystemEvents.DueChanged, TaskSystemEvents.StartChanged);
    }

    [Fact]
    public async Task G7_Patch_DueOnly_BeforeStart_StartFollows()
    {
        As("sales01");
        var created = await MustCreate(D(20), D(25));

        var r = await Patch(created.Id, """{"dueDate":"2026-11-19"}""");

        r.IsSuccess.Should().BeTrue(r.Message);
        var detail = (TaskDetailResponse)r.Data!;
        (detail.StartDate, detail.DueDate).Should().Be((D(19), D(19)));
        var entries = await Entries(created.Id);
        entries.Select(c => c.SystemEvent).Should().Equal(TaskSystemEvents.Created, TaskSystemEvents.DueChanged, TaskSystemEvents.StartFollowedDue);
        var follow = entries.Last();
        follow.AuthorUserId.Should().Be("sales01");
        J(follow.SystemData!).GetRawText().Should().Be("""{"from":"2026-11-20","to":"2026-11-19"}""");
    }

    [Fact]
    public async Task Patch_DueOnly_LaterOrEqual_StartUntouched()
    {
        var created = await MustCreate(D(20), D(25));

        (await Patch(created.Id, """{"dueDate":"2026-11-20"}""")).IsSuccess.Should().BeTrue();

        (await Load(created.Id)).StartDate.Should().Be(D(20));
        (await Entries(created.Id)).Select(c => c.SystemEvent).Should().NotContain(TaskSystemEvents.StartFollowedDue);
    }

    [Fact]
    public async Task G8_Patch_ClearDue_StartUntouched()
    {
        var created = await MustCreate(D(20), D(25));

        (await Patch(created.Id, """{"dueDate":null}""")).IsSuccess.Should().BeTrue();

        var after = await Load(created.Id);
        after.DueDate.Should().BeNull();
        after.StartDate.Should().Be(D(20));
    }

    [Fact]
    public async Task G11_Dto_DetailListAndLegacyNull()
    {
        var legacy = await SeedTask("升級前的任務", createdBy: "admin", assignee: "admin");
        var withStart = await SeedTask("有開始日", createdBy: "admin", assignee: "admin", due: D(25), start: D(20));

        var detail = (TaskDetailResponse)(await Send(new GetTaskRequest(withStart.Id))).Data!;
        detail.StartDate.Should().Be(D(20));
        ((TaskDetailResponse)(await Send(new GetTaskRequest(legacy.Id))).Data!).StartDate.Should().BeNull();

        var list = (PagedResponse<TaskSummaryResponse>)(await Send(new GetMyTasksRequest("assigned", "all", 1, 20))).Data!;
        list.Items.Single(i => i.Id == withStart.Id).StartDate.Should().Be(D(20));
        list.Items.Single(i => i.Id == legacy.Id).StartDate.Should().BeNull();
    }
}
