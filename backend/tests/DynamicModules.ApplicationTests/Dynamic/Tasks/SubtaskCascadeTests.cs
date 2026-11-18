using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Tasks;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 子任務狀態連動 st1b（看板設計 §10.7.4、§10.7.5 C20–C34）：子任務帶動父任務、父任務帶動子任務、cascade（PATCH 與看板移動）、
/// 記錄刪除不連動、連動途中失敗整筆回滾。規則路徑的 C29 與規則回滾在 <see cref="TaskRuleRuntimeTests"/>。
/// </summary>
public class SubtaskCascadeTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private readonly FailingTaskNotifier.Switch _fail = new();

    protected override void ConfigureServices(IServiceCollection services) => FailingTaskNotifier.Register(services, _fail);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>每個請求前後清掉共用 DbContext 的追蹤器，與正式環境每個請求一個 DbContext 一樣。</summary>
    private async Task<ServiceResult> Run(string user, MediatR.IRequest<ServiceResult> request)
    {
        Db.Context.ChangeTracker.Clear();
        As(user);
        try { return await Send(request); }
        finally
        {
            As("admin");
            Db.Context.ChangeTracker.Clear();
        }
    }

    private static string Dump(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + "=" + e.Value[0]));

    private Task<ServiceResult> Patch(string user, long id, string json) => Run(user, new UpdateTaskRequest(id, J(json)));

    private async Task MustPatch(string user, long id, string json)
    {
        var r = await Patch(user, id, json);
        r.IsSuccess.Should().BeTrue(Dump(r));
    }

    private async Task<TaskItem> SeedChild(TaskItem parent, string title, string? createdBy = "sales01", string? assignee = null,
        TaskItemStatus status = TaskItemStatus.Todo, bool cancelledByParent = false)
    {
        var child = await SeedTask(title, createdBy: createdBy, assignee: assignee, status: status, recordId: parent.RecordId,
            moduleId: parent.ModuleId);
        await Db.Context.Set<TaskItem>().Where(t => t.Id == child.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.ParentTaskId, parent.Id).SetProperty(t => t.CancelledByParent, cancelledByParent));
        child.ParentTaskId = parent.Id;
        return child;
    }

    private Task<List<TaskComment>> SystemEntries(long taskId) =>
        Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId && c.Kind == TaskCommentKind.System)
            .OrderBy(c => c.Id).ToListAsync();

    private async Task<TaskComment> SingleEntry(long taskId, string systemEvent) =>
        (await SystemEntries(taskId)).Should().ContainSingle(c => c.SystemEvent == systemEvent).Subject;

    private Task<List<Notification>> Inbox(string userId, string? kind = null) =>
        Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId && (kind == null || n.Kind == kind))
            .OrderBy(n => n.Id).ToListAsync();

    private static Dictionary<string, JsonElement> Data(TaskComment c) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.SystemData!)!;

    // ───────── 子任務帶動父任務 ─────────

    [Fact]
    public async Task C20_ChildStarts_TodoParentBecomesInProgress_AsSystem_EvenWithoutParentEdit()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", assignee: "sales02");
        (await Run("sales02", new GetTaskRequest(parent.Id))).IsNotFound.Should().BeTrue("sales02 看不到、也不能改父任務");

        await MustPatch("sales02", child.Id, """{"status":"inProgress"}""");

        var p = await Load(parent.Id);
        p.Status.Should().Be(TaskItemStatus.InProgress);
        p.ModifiedBy.Should().BeNull("連動算系統事件（T25）");
        var entry = await SingleEntry(parent.Id, TaskSystemEvents.BySubtask);
        entry.AuthorUserId.Should().BeNull();
        var d = Data(entry);
        d["from"].GetString().Should().Be("todo");
        d["to"].GetString().Should().Be("inProgress");
        d["subtaskId"].GetInt64().Should().Be(child.Id);
        d["subtaskTitle"].GetString().Should().Be("訂場地");
        (await Inbox("sales01")).Should().BeEmpty("開工帶動父任務不發通知");
    }

    [Theory]
    [InlineData(TaskItemStatus.InProgress)]
    [InlineData(TaskItemStatus.Done)]
    [InlineData(TaskItemStatus.Cancelled)]
    public async Task C20_ChildStarts_ParentNotTodo_Untouched(TaskItemStatus parentStatus)
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: parentStatus);
        var child = await SeedChild(parent, "訂場地", assignee: "sales02");

        await MustPatch("sales02", child.Id, """{"status":"inProgress"}""");

        (await Load(parent.Id)).Status.Should().Be(parentStatus);
        (await SystemEntries(parent.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task C21_LastChildDone_ParentStays_SubtasksAllDone_NotifiesParentAssignee()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01", status: TaskItemStatus.InProgress);
        await SeedChild(parent, "訂場地", createdBy: "admin", status: TaskItemStatus.Done);
        var c2 = await SeedChild(parent, "印海報", createdBy: "admin", assignee: "sales02", status: TaskItemStatus.InProgress);

        await MustPatch("sales02", c2.Id, """{"status":"done"}""");

        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.InProgress, "父任務不自動完成");
        var entry = await SingleEntry(parent.Id, TaskSystemEvents.SubtasksAllDone);
        entry.AuthorUserId.Should().BeNull();
        Data(entry)["subtaskId"].GetInt64().Should().Be(c2.Id);
        Data(entry)["subtaskTitle"].GetString().Should().Be("印海報");
        var mail = (await Inbox("sales01", NotificationKinds.TaskSubtasksDone)).Should().ContainSingle().Subject;
        mail.SourceId.Should().Be(parent.Id);
        mail.TitleSnapshot.Should().Be("辦行銷活動");
        (await Inbox("admin", NotificationKinds.TaskSubtasksDone)).Should().BeEmpty("A40 出廠只通知父任務負責人（ST-Q6）");
    }

    [Fact]
    public async Task C21_ParentAssigneeFinishesLastChild_EntryWritten_NoNotification()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01");
        await SeedChild(parent, "訂場地", createdBy: "admin", status: TaskItemStatus.Done);
        var c2 = await SeedChild(parent, "印海報", createdBy: "admin", assignee: "sales02", status: TaskItemStatus.InProgress);

        await MustPatch("sales01", c2.Id, """{"status":"done"}""");

        await SingleEntry(parent.Id, TaskSystemEvents.SubtasksAllDone);
        (await Inbox("sales01", NotificationKinds.TaskSubtasksDone)).Should().BeEmpty("不通知操作者本人");
    }

    [Fact]
    public async Task C21_LastChildCancelled_WithOneDone_StillTriggers()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01");
        await SeedChild(parent, "訂場地", createdBy: "admin", status: TaskItemStatus.Done);
        var c2 = await SeedChild(parent, "印海報", createdBy: "admin", assignee: "sales02");

        await MustPatch("sales02", c2.Id, """{"status":"cancelled"}""");

        await SingleEntry(parent.Id, TaskSystemEvents.SubtasksAllDone);
        (await Inbox("sales01", NotificationKinds.TaskSubtasksDone)).Should().ContainSingle();
    }

    [Fact]
    public async Task C21_AllChildrenCancelled_DoesNotTrigger()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01");
        await SeedChild(parent, "訂場地", createdBy: "admin", status: TaskItemStatus.Cancelled);
        var c2 = await SeedChild(parent, "印海報", createdBy: "admin", assignee: "sales02");

        await MustPatch("sales02", c2.Id, """{"status":"cancelled"}""");

        (await SystemEntries(parent.Id)).Should().BeEmpty("全部取消沒有東西做完（ST-Q5）");
        (await Inbox("sales01", NotificationKinds.TaskSubtasksDone)).Should().BeEmpty();
    }

    [Fact]
    public async Task C21_OtherChildStillOpen_DoesNotTrigger_AndPolicyCanAddCreator()
    {
        Db.Context.Add(new SystemSetting { Key = PolicyCatalog.TaskNotifySubtasksDone.SettingKey, Value = "assignee,creator" });
        await Db.Context.SaveChangesAsync();
        var parent = await SeedTask("辦行銷活動", createdBy: "sales02", assignee: "sales01");
        var c1 = await SeedChild(parent, "訂場地", createdBy: "admin", assignee: "sales02");
        var c2 = await SeedChild(parent, "印海報", createdBy: "admin", assignee: "sales02");

        await MustPatch("sales02", c1.Id, """{"status":"done"}""");
        (await SystemEntries(parent.Id)).Should().NotContain(c => c.SystemEvent == TaskSystemEvents.SubtasksAllDone);

        await MustPatch("admin", c2.Id, """{"status":"done"}""");
        (await Inbox("sales01", NotificationKinds.TaskSubtasksDone)).Should().ContainSingle();
        (await Inbox("sales02", NotificationKinds.TaskSubtasksDone)).Should().ContainSingle("政策加上建立者後父任務建立者也收到");
    }

    [Fact]
    public async Task C21_ParentAlreadyDone_DoesNotTrigger()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01", status: TaskItemStatus.Done);
        var c1 = await SeedChild(parent, "訂場地", createdBy: "admin", assignee: "sales02", status: TaskItemStatus.InProgress);

        await MustPatch("sales02", c1.Id, """{"status":"done"}""");

        (await SystemEntries(parent.Id)).Should().BeEmpty();
    }

    // ───────── 父任務改成完成：cascade ─────────

    [Fact]
    public async Task C22_ParentDone_WithOpenChildren_409SubtasksOpen_NothingWritten()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c1 = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "sales02");
        var c2 = await SeedChild(parent, "印海報", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.InProgress);
        await SeedChild(parent, "已完成", createdBy: "sales02", status: TaskItemStatus.Done);
        var version = TaskVersion.Of(await Load(parent.Id));

        var r = await Patch("sales01", parent.Id, """{"status":"done","title":"改標題"}""");

        r.IsConflict.Should().BeTrue();
        r.Code.Should().Be("subtasksOpen");
        r.Message.Should().Be("還有 2 個未完成的子任務");
        JsonSerializer.SerializeToElement(r.Params).GetProperty("count").GetInt32().Should().Be(2);
        var p = await Load(parent.Id);
        p.Status.Should().Be(TaskItemStatus.InProgress);
        p.Title.Should().Be("辦行銷活動", "整筆不寫入");
        TaskVersion.Of(p).Should().Be(version);
        (await Load(c1.Id)).Status.Should().Be(TaskItemStatus.Todo);
        (await Load(c2.Id)).Status.Should().Be(TaskItemStatus.InProgress);
        (await SystemEntries(parent.Id)).Should().BeEmpty();
        (await SystemEntries(c1.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task C23_CascadeComplete_ChildrenDoneByOperator_NotifiedPerA13()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c1 = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "sales02");
        var c2 = await SeedChild(parent, "印海報", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.InProgress);
        var c3 = await SeedChild(parent, "自己建的", createdBy: "sales01", assignee: "sales02");

        await MustPatch("sales01", parent.Id, """{"status":"done","cascade":"complete"}""");

        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.Done);
        foreach (var c in new[] { c1, c2, c3 })
        {
            var stored = await Load(c.Id);
            stored.Status.Should().Be(TaskItemStatus.Done);
            stored.CompletedTime.Should().NotBeNull();
            stored.ModifiedBy.Should().Be("sales01");
            stored.CancelledByParent.Should().BeFalse();
            var entry = await SingleEntry(c.Id, TaskSystemEvents.ByParent);
            entry.AuthorUserId.Should().Be("sales01");
            var d = Data(entry);
            d["to"].GetString().Should().Be("done");
            d["parentId"].GetInt64().Should().Be(parent.Id);
            d["parentTitle"].GetString().Should().Be("辦行銷活動");
        }
        (await Inbox("sales02", NotificationKinds.TaskCompleted)).Select(n => n.SourceId).Should().BeEquivalentTo([c1.Id, c2.Id],
            "A13 出廠通知建立者；sales01 自己建的 c3 不通知操作者本人");
        (await Inbox("sales01")).Should().BeEmpty();
        (await SystemEntries(parent.Id)).Should().NotContain(c => c.SystemEvent == TaskSystemEvents.SubtasksAllDone,
            "父任務帶動的子任務不反過來連動（ST-T11）");
    }

    [Fact]
    public async Task C24_CascadeCancel_NoFlag_ReopenParentDoesNotRestore()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c1 = await SeedChild(parent, "訂場地", assignee: "sales02");
        var c2 = await SeedChild(parent, "印海報", assignee: "sales02");

        await MustPatch("sales01", parent.Id, """{"status":"done","cascade":"cancel"}""");

        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.Done);
        foreach (var c in new[] { c1, c2 })
        {
            var stored = await Load(c.Id);
            stored.Status.Should().Be(TaskItemStatus.Cancelled);
            stored.CancelledByParent.Should().BeFalse("父任務不是取消（ST-T5）");
            stored.ModifiedBy.Should().Be("sales01");
            (await SingleEntry(c.Id, TaskSystemEvents.ByParent)).AuthorUserId.Should().Be("sales01");
        }

        await MustPatch("sales01", parent.Id, """{"status":"inProgress"}""");
        (await Load(c1.Id)).Status.Should().Be(TaskItemStatus.Cancelled);
        (await Load(c2.Id)).Status.Should().Be(TaskItemStatus.Cancelled);
    }

    [Fact]
    public async Task Cascade_BadValue_400()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");

        var r = await Patch("sales01", parent.Id, """{"status":"done","cascade":"drop"}""");

        r.IsInvalid.Should().BeTrue();
        Err(r, "cascade").Should().Be(TaskMessages.BadCascade);
    }

    [Fact]
    public async Task Cascade_IgnoredWhenNotNeeded()
    {
        // 沒有未完成的子任務、不是改狀態、是子任務：帶了也忽略（ST-T4）
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var done = await SeedChild(parent, "訂場地", assignee: "sales02", status: TaskItemStatus.Done);
        var lone = await SeedTask("獨立任務", createdBy: "sales01", assignee: "sales01");

        await MustPatch("sales01", lone.Id, """{"status":"done","cascade":"cancel"}""");
        await MustPatch("sales01", parent.Id, """{"title":"新標題","cascade":"cancel"}""");
        await MustPatch("sales02", done.Id, """{"title":"訂場地二","cascade":"complete"}""");
        await MustPatch("sales01", parent.Id, """{"status":"done","cascade":"cancel"}""");

        (await Load(lone.Id)).Status.Should().Be(TaskItemStatus.Done);
        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.Done);
        (await Load(done.Id)).Status.Should().Be(TaskItemStatus.Done, "已完成的子任務不被 cancel 改掉");
        (await SystemEntries(done.Id)).Should().NotContain(c => c.SystemEvent == TaskSystemEvents.ByParent);
    }

    [Fact]
    public async Task Cascade_SupervisorBlockedByAssigneeOnlyRule()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales02");
        await SeedChild(parent, "訂場地", assignee: "sales02");

        var r = await Patch("manager01", parent.Id, """{"status":"done","cascade":"complete"}""");

        r.IsForbidden.Should().BeTrue();
        r.Message.Should().Be(TaskMessages.SupervisorOnlyAssignee);
    }

    [Fact]
    public async Task C25_BoardDropToDone_409ThenRetryWithSameVersion()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c1 = await SeedChild(parent, "訂場地", assignee: "sales02");
        var c2 = await SeedChild(parent, "印海報", assignee: "sales02");
        var version = TaskVersion.Of(await Load(parent.Id));

        var refused = await Run("sales01", new MoveTaskRequest(parent.Id, "status", "done", null, version));
        refused.IsConflict.Should().BeTrue();
        refused.Code.Should().Be("subtasksOpen");
        (await Load(c1.Id)).Status.Should().Be(TaskItemStatus.Todo);

        var bad = await Run("sales01", new MoveTaskRequest(parent.Id, "status", "done", null, version, "nope"));
        bad.IsInvalid.Should().BeTrue();
        Err(bad, "cascade").Should().Be(TaskMessages.BadCascade);

        var r = await Run("sales01", new MoveTaskRequest(parent.Id, "status", "done", null, version, "complete"));
        r.IsSuccess.Should().BeTrue(Dump(r));
        var card = ((TaskMoveResponse)r.Data!).Card!;
        card.Status.Should().Be(TaskItemStatus.Done);
        card.Progress.Should().Be(new TaskProgress(2, 0, 2));
        (await Load(c2.Id)).Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public async Task Move_CascadeIgnoredOutsideStatusBoard()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", assignee: "sales02");

        var r = await Run("sales01", new MoveTaskRequest(parent.Id, "assignee", "user:sales02", null, TaskVersion.Of(await Load(parent.Id)), "cancel"));

        r.IsSuccess.Should().BeTrue(Dump(r));
        (await Load(child.Id)).Status.Should().Be(TaskItemStatus.Todo);
    }

    // ───────── 父任務取消與恢復 ─────────

    [Fact]
    public async Task C26_C27_ParentCancelled_OpenChildrenCancelledWithFlag_RestoreOnlyThose()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c1 = await SeedChild(parent, "訂場地", createdBy: "sales02", status: TaskItemStatus.Done);
        var c2 = await SeedChild(parent, "印海報", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.InProgress);
        var c3 = await SeedChild(parent, "手動取消", createdBy: "sales02", status: TaskItemStatus.Cancelled);

        await MustPatch("sales01", parent.Id, """{"status":"cancelled"}""");

        var cancelled = await Load(c2.Id);
        cancelled.Status.Should().Be(TaskItemStatus.Cancelled);
        cancelled.CancelledByParent.Should().BeTrue();
        cancelled.ModifiedBy.Should().BeNull();
        var entry = await SingleEntry(c2.Id, TaskSystemEvents.ByParent);
        entry.AuthorUserId.Should().BeNull();
        Data(entry)["from"].GetString().Should().Be("inProgress");
        Data(entry)["to"].GetString().Should().Be("cancelled");
        (await Load(c1.Id)).Status.Should().Be(TaskItemStatus.Done);
        (await Load(c3.Id)).CancelledByParent.Should().BeFalse();
        (await SystemEntries(c1.Id)).Should().BeEmpty();
        (await SystemEntries(c3.Id)).Should().BeEmpty();
        (await Inbox("sales02", NotificationKinds.TaskCancelled)).Select(n => n.SourceId).Should().Equal(c2.Id);

        // C27：恢復成待辦，因父任務取消的 C2 變待辦（不是原本的進行中）、旗標清掉；C3 仍取消；恢復不發通知
        var before = (await Inbox("sales02")).Count;
        await MustPatch("sales01", parent.Id, """{"status":"todo"}""");

        var restored = await Load(c2.Id);
        restored.Status.Should().Be(TaskItemStatus.Todo);
        restored.CancelledByParent.Should().BeFalse();
        restored.ModifiedBy.Should().BeNull();
        (await SystemEntries(c2.Id)).Last().SystemEvent.Should().Be(TaskSystemEvents.ByParent);
        (await Load(c3.Id)).Status.Should().Be(TaskItemStatus.Cancelled);
        (await Load(c1.Id)).Status.Should().Be(TaskItemStatus.Done);
        (await Inbox("sales02")).Should().HaveCount(before);
    }

    [Fact]
    public async Task C28_CancelledParentStraightToDone_ChildrenStayCancelled_FlagCleared()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var c2 = await SeedChild(parent, "印海報", assignee: "sales02", status: TaskItemStatus.InProgress);
        await MustPatch("sales01", parent.Id, """{"status":"cancelled"}""");

        await MustPatch("sales01", parent.Id, """{"status":"done"}""");

        var stored = await Load(c2.Id);
        stored.Status.Should().Be(TaskItemStatus.Cancelled);
        stored.CancelledByParent.Should().BeFalse();

        await MustPatch("sales01", parent.Id, """{"status":"inProgress"}""");
        (await Load(c2.Id)).Status.Should().Be(TaskItemStatus.Cancelled);
    }

    [Fact]
    public async Task C30_ManuallyCancelledRuleParent_ManualRestore_RestoresChildren()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var ruleId = await SeedRule(t, "tk-rule", "活動規則");
        var parent = await SeedTask("規劃活動", createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: ruleId);
        var child = await SeedChild(parent, "訂場地", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);

        await MustPatch("sales01", parent.Id, """{"status":"cancelled"}""");
        var p = await Load(parent.Id);
        p.CancelledByRule.Should().BeFalse();
        (await Load(child.Id)).CancelledByParent.Should().BeTrue();

        await MustPatch("sales01", parent.Id, """{"status":"todo"}""");

        var c = await Load(child.Id);
        c.Status.Should().Be(TaskItemStatus.Todo);
        c.CancelledByParent.Should().BeFalse();
    }

    // ───────── 重開子任務 ─────────

    [Fact]
    public async Task C32_ReopenChildUnderDoneParent_ParentBackInProgress()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Done);
        var c1 = await SeedChild(parent, "訂場地", assignee: "sales02", status: TaskItemStatus.Done);
        await SeedChild(parent, "印海報", assignee: "sales02", status: TaskItemStatus.Done);

        await MustPatch("sales02", c1.Id, """{"status":"todo"}""");

        var p = await Load(parent.Id);
        p.Status.Should().Be(TaskItemStatus.InProgress);
        p.CompletedTime.Should().BeNull();
        p.ModifiedBy.Should().BeNull();
        var entry = await SingleEntry(parent.Id, TaskSystemEvents.BySubtask);
        entry.AuthorUserId.Should().BeNull();
        Data(entry)["from"].GetString().Should().Be("done");
        Data(entry)["to"].GetString().Should().Be("inProgress");
        Data(entry)["subtaskId"].GetInt64().Should().Be(c1.Id);
    }

    [Fact]
    public async Task C33_ReopenChildUnderCancelledParent_409_ChildUnchanged()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Cancelled);
        var child = await SeedChild(parent, "訂場地", assignee: "sales02", status: TaskItemStatus.Cancelled, cancelledByParent: true);

        var r = await Patch("sales02", child.Id, """{"status":"todo"}""");

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("父任務已取消，請先恢復父任務");
        var c = await Load(child.Id);
        c.Status.Should().Be(TaskItemStatus.Cancelled);
        c.CancelledByParent.Should().BeTrue();
        (await SystemEntries(child.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ManualChildStatusChange_ClearsCancelledByParent()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", assignee: "sales02", status: TaskItemStatus.Cancelled, cancelledByParent: true);

        await MustPatch("sales02", child.Id, """{"status":"done"}""");

        (await Load(child.Id)).CancelledByParent.Should().BeFalse("人手動改狀態清旗標（§10.7.4 第 8 項）");
    }

    // ───────── 記錄刪除與回滾 ─────────

    private async Task<string> RowVersion(long recordId)
    {
        Db.Context.ChangeTracker.Clear();
        return (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == recordId)).RowVersion;
    }

    [Fact]
    public async Task C31_RecordDeleted_BothRecordDeleted_NoParentFlag()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec, status: TaskItemStatus.InProgress);
        var child = await SeedChild(parent, "訂場地", assignee: "sales01", status: TaskItemStatus.InProgress);

        var r = await Run("admin", new DeleteRecordRequest(rec, await RowVersion(rec)));
        r.IsSuccess.Should().BeTrue(Dump(r));

        foreach (var id in new[] { parent.Id, child.Id })
        {
            var task = await Load(id);
            task.Status.Should().Be(TaskItemStatus.Cancelled);
            task.CancelledByParent.Should().BeFalse("記錄刪除不可恢復（ST-T6）");
            (await SystemEntries(id)).Select(c => c.SystemEvent).Should().Equal(TaskSystemEvents.RecordDeleted);
        }
    }

    [Fact]
    public async Task C34_FailureMidCascade_RollsBackParentAndChildren_NoNotifications()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var child = await SeedChild(parent, "印海報", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.InProgress);
        _fail.FailWhen = c => c.Task.Id == child.Id;

        var act = () => Patch("sales01", parent.Id, """{"status":"cancelled"}""");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _fail.FailWhen = null;
        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.InProgress);
        var c = await Load(child.Id);
        c.Status.Should().Be(TaskItemStatus.InProgress);
        c.CancelledByParent.Should().BeFalse();
        (await SystemEntries(parent.Id)).Should().BeEmpty();
        (await SystemEntries(child.Id)).Should().BeEmpty();
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task C34_FailureInRecordDeletePath_RollsBackRecordAndTasks()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales02", assignee: "sales01", target: t, recordId: rec);
        var child = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "sales01");
        var parentEntries = (await SystemEntries(parent.Id)).Count;
        var childEntries = (await SystemEntries(child.Id)).Count;
        _fail.FailWhen = c => c.Task.Id == child.Id;

        var act = async () => await Run("admin", new DeleteRecordRequest(rec, await RowVersion(rec)));

        await act.Should().ThrowAsync<InvalidOperationException>();
        _fail.FailWhen = null;
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == rec)).DeleteFlag.Should().BeFalse();
        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.Todo);
        (await Load(child.Id)).Status.Should().Be(TaskItemStatus.Todo);
        (await SystemEntries(parent.Id)).Should().HaveCount(parentEntries, "recordDeleted 系統記錄一起回滾");
        (await SystemEntries(child.Id)).Should().HaveCount(childEntries);
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task C34_NotificationsAlreadySaved_ThenFailure_RollBackToo()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "admin", assignee: "sales01", status: TaskItemStatus.InProgress);
        var child = await SeedChild(parent, "印海報", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.InProgress);
        _fail.FailWhen = c => c.Task.Id == child.Id;
        _fail.FailAfterSave = true;

        var act = () => Patch("sales01", parent.Id, """{"status":"cancelled"}""");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _fail.FailWhen = null;
        _fail.FailAfterSave = false;
        _fail.SavedBeforeFailure.Should().BeGreaterThanOrEqualTo(1, "真正的寫入端已寫通知並 SaveChanges（父任務建立者 admin、子任務建立者 sales02）");
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync()).Should().Be(0, "已存進交易的通知也一起回滾");
        (await Load(parent.Id)).Status.Should().Be(TaskItemStatus.InProgress);
        var c = await Load(child.Id);
        c.Status.Should().Be(TaskItemStatus.InProgress);
        c.CancelledByParent.Should().BeFalse();
        (await SystemEntries(parent.Id)).Should().BeEmpty();
        (await SystemEntries(child.Id)).Should().BeEmpty();
    }
}
