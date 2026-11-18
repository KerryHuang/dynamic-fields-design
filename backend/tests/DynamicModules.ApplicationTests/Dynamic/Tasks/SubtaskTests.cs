using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Tasks;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 子任務 st1a（看板設計 §10.7）：新增與限制（C1–C11）、可見與權限（C12–C16）、刪除（C35–C37）、記錄頁與進度（C41、C42）。
/// 示範組織：manager01 管全公司（遞迴）；outsider01 沒有部門、不在任何群組。
/// </summary>
public class SubtaskTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private const string Outsider = "outsider01";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        Db.Context.Add(new AppUser { UserId = Outsider, DisplayName = "外部人員", IsActive = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<ServiceResult> SendAsUser(string user, MediatR.IRequest<ServiceResult> request)
    {
        As(user);
        var r = await Send(request);
        As("admin");
        return r;
    }

    private Task<ServiceResult> AddSub(string user, long parentId, string title = "訂場地", string? assignee = null) =>
        SendAsUser(user, new CreateSubtaskRequest(parentId, new TaskInput(title, null, assignee, null)));

    private async Task<TaskDetailResponse> Detail(string user, long id)
    {
        var r = await SendAsUser(user, new GetTaskRequest(id));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (TaskDetailResponse)r.Data!;
    }

    private async Task SetPolicy(PolicyDefinition def, string value)
    {
        Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>直接在 Db 建子任務（記錄、模組照父任務）。</summary>
    private async Task<TaskItem> SeedChild(TaskItem parent, string title, string? createdBy = "admin", string? assignee = null,
        TaskItemStatus status = TaskItemStatus.Todo)
    {
        var child = await SeedTask(title, createdBy: createdBy, assignee: assignee, status: status, recordId: parent.RecordId,
            moduleId: parent.ModuleId);
        await Db.Context.Set<TaskItem>().Where(t => t.Id == child.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ParentTaskId, parent.Id));
        child.ParentTaskId = parent.Id;
        return child;
    }

    /// <summary>tk2：只有業務群組 Own，業務主管沒有任何權限。</summary>
    private async Task<Target> BuildRestrictedTarget(string key)
    {
        var t = await BuildTarget(key);
        var r = await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [new GroupPermissionDto(t.SalesGroupId, new PrivilegesDto(true, false, true, false, false, false, false), DataScope.Own)],
            [], [], [])));
        r.IsSuccess.Should().BeTrue(r.Message);
        return t;
    }

    private Task<List<TaskComment>> SystemEntries(long taskId) =>
        Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId && c.Kind == TaskCommentKind.System)
            .OrderBy(c => c.Id).ToListAsync();

    private static (long Id, string Title) Data(TaskComment c)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(c.SystemData!);
        return (doc.RootElement.GetProperty("subtaskId").GetInt64(), doc.RootElement.GetProperty("subtaskTitle").GetString()!);
    }

    // ---- 建立與限制 ----

    [Fact]
    public async Task C1_Create_CopiesRecord_ParentShowsProgress_AuditEntryWithoutVersionChange()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales02", target: t, recordId: rec);
        var versionBefore = TaskVersion.Of(await Load(parent.Id));

        var r = await AddSub("sales02", parent.Id, "訂場地", "sales01");

        r.IsCreated.Should().BeTrue(r.Message);
        var child = (TaskDetailResponse)r.Data!;
        child.ParentTaskId.Should().Be(parent.Id);
        child.ParentTitle.Should().Be("辦行銷活動");
        child.ParentVisible.Should().BeTrue();
        child.Level.Should().Be(TaskLevel.Child);
        child.CreatedBy.Should().Be("sales02");
        child.AssigneeUserId.Should().Be("sales01");
        child.Progress.Should().BeNull();
        child.Subtasks.Should().BeEmpty();
        child.CanAddSubtask.Should().BeFalse("子任務不能再有子任務");
        var stored = await Load(child.Id);
        stored.ModuleId.Should().Be(t.ModuleId);
        stored.RecordId.Should().Be(rec);
        stored.RecordTitleSnapshot.Should().Be("案件甲");
        stored.Participants.Select(p => (p.UserId, p.Reason)).Should().BeEquivalentTo(
            [("sales02", TaskParticipantReason.Creator), ("sales01", TaskParticipantReason.Assignee)]);

        var p = await Detail("sales01", parent.Id);
        p.Level.Should().Be(TaskLevel.Parent);
        p.Progress.Should().Be(new TaskProgress(0, 0, 1));
        p.Subtasks.Should().ContainSingle().Which.Id.Should().Be(child.Id);
        p.Subtasks[0].Level.Should().Be(TaskLevel.Child);
        p.Subtasks[0].ParentTaskId.Should().Be(parent.Id);
        p.CanAddSubtask.Should().BeTrue();
        p.Version.Should().Be(versionBefore, "subtaskAdded 只是稽核記錄，不改父任務版本");
        var entry = (await SystemEntries(parent.Id)).Should().ContainSingle(c => c.SystemEvent == TaskSystemEvents.SubtaskAdded).Subject;
        entry.AuthorUserId.Should().Be("sales02");
        Data(entry).Should().Be((child.Id, "訂場地"));
    }

    [Fact]
    public async Task C2_PersonalParent_ChildIsPersonal()
    {
        var parent = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var r = await AddSub("sales01", parent.Id);

        r.IsCreated.Should().BeTrue(r.Message);
        var child = (TaskDetailResponse)r.Data!;
        child.Record.Should().BeNull();
        child.AssigneeUserId.Should().BeNull("後端不補負責人預設值（ST-T2）");
        (await Load(child.Id)).ModuleId.Should().BeNull();
    }

    [Fact]
    public async Task C3_ChildCannotHaveChildren_400Parent()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01");
        var child = await SeedChild(parent, "訂場地", createdBy: "sales01");

        var r = await AddSub("sales01", child.Id);

        r.IsInvalid.Should().BeTrue();
        Err(r, "parent").Should().Be(TaskMessages.SubtaskOneLevel);
    }

    [Theory]
    [InlineData(TaskItemStatus.Done)]
    [InlineData(TaskItemStatus.Cancelled)]
    public async Task C4_ClosedParent_409ParentClosed(TaskItemStatus status)
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", status: status);

        var r = await AddSub("sales01", parent.Id);

        r.IsConflict.Should().BeTrue();
        r.Code.Should().Be("parentClosed");
        r.Message.Should().Be(TaskMessages.SubtaskParentClosed);
        (await Detail("sales01", parent.Id)).CanAddSubtask.Should().BeFalse();
    }

    [Fact]
    public async Task C5_Limit_CountsDoneAndCancelled()
    {
        await SetPolicy(PolicyCatalog.TaskMaxSubtasksPerTask, "2");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01");
        await SeedChild(parent, "已完成", status: TaskItemStatus.Done);
        await SeedChild(parent, "已取消", status: TaskItemStatus.Cancelled);

        var r = await AddSub("sales01", parent.Id);

        r.IsInvalid.Should().BeTrue();
        Err(r, "subtasks").Should().Be("一個任務最多 2 個子任務");
        (await Db.Context.Set<TaskItem>().CountAsync(x => x.ParentTaskId == parent.Id)).Should().Be(2);
    }

    [Fact]
    public async Task C6_LimitTightened_ExistingUntouched_OnlyNewBlocked()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01");
        var children = new List<TaskItem>();
        for (var i = 1; i <= 3; i++) children.Add(await SeedChild(parent, $"子任務{i}", createdBy: "sales01"));
        await SetPolicy(PolicyCatalog.TaskMaxSubtasksPerTask, "2");

        var detail = await Detail("sales01", parent.Id);
        detail.Subtasks.Should().HaveCount(3);
        foreach (var c in children)
            (await SendAsUser("sales01", new UpdateTaskRequest(c.Id, J("""{"status":"done"}""")))).IsSuccess.Should().BeTrue();
        var r = await AddSub("sales01", parent.Id);

        r.IsInvalid.Should().BeTrue();
        r.Errors.Should().ContainKey("subtasks");
    }

    [Fact]
    public async Task C7_SupervisorOnly_403_CanAddSubtaskFalse()
    {
        var parent = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var detail = await Detail("manager01", parent.Id);
        var r = await AddSub("manager01", parent.Id);

        detail.Supervised.Should().BeTrue();
        detail.CanAddSubtask.Should().BeFalse();
        r.IsForbidden.Should().BeTrue();
        r.Message.Should().Be(TaskMessages.SubtaskNeedsParentEdit);
        (await Db.Context.Set<TaskItem>().CountAsync(x => x.ParentTaskId == parent.Id)).Should().Be(0);
    }

    [Fact]
    public async Task C8_Invisible_404()
    {
        var parent = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var hidden = await AddSub(Outsider, parent.Id);
        var missing = await AddSub(Outsider, parent.Id + 1000);

        hidden.IsNotFound.Should().BeTrue();
        missing.IsNotFound.Should().BeTrue();
        hidden.Message.Should().Be(missing.Message);
    }

    [Fact]
    public async Task C9_Frozen_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Id == t.ModuleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));

        var r = await AddSub("sales01", parent.Id);

        r.IsConflict.Should().BeTrue();
        r.Code.Should().BeNull();
        (await Detail("sales01", parent.Id)).CanAddSubtask.Should().BeFalse();
    }

    [Fact]
    public async Task C10_ModuleTasksDisabled_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Id == t.ModuleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.TasksEnabled, false));

        var r = await AddSub("sales01", parent.Id);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("這個模組未啟用任務");
        (await Detail("sales01", parent.Id)).CanAddSubtask.Should().BeFalse();
    }

    [Fact]
    public async Task C11_RuleTaskParent_CanHaveManualSubtask()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var ruleId = await SeedRule(t, "follow", "追蹤");
        var parent = await SeedTask("追蹤", createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: ruleId);

        var r = await AddSub("sales01", parent.Id);

        r.IsCreated.Should().BeTrue(r.Message);
        var child = (TaskDetailResponse)r.Data!;
        child.RuleKey.Should().BeNull();
        (await Load(child.Id)).RuleId.Should().BeNull();
        (await Detail("sales01", parent.Id)).Subtasks.Single().FromRule.Should().BeFalse();
    }

    // ---- 可見與權限 ----

    [Fact]
    public async Task C12_OnlyChildVisible_ParentTitleShown_ParentIs404()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "印海報", createdBy: "sales01", assignee: "sales02");

        var c = await Detail("sales02", child.Id);
        var p = await SendAsUser("sales02", new GetTaskRequest(parent.Id));

        c.ParentTitle.Should().Be("辦行銷活動");
        c.ParentVisible.Should().BeFalse();
        c.CanEdit.Should().BeTrue();
        p.IsNotFound.Should().BeTrue();
        // 不自動成為父任務的參與者（§10.3）
        (await Load(parent.Id)).Participants.Select(x => x.UserId).Should().NotContain("sales02");
    }

    [Fact]
    public async Task C13_ParentParticipant_SeesAllChildren_NotInMentionedTab()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01", mentioned: ["sales02"]);
        var child = await SeedChild(parent, "訂便當", createdBy: "sales01", assignee: "sales01");

        var c = await Detail("sales02", child.Id);
        var mentioned = await SendAsUser("sales02", new GetMyTasksRequest("mentioned", "all", 1, 50));

        c.ParentVisible.Should().BeTrue();
        c.CanEdit.Should().BeFalse("只因看得到父任務而看得到，不是能改的人");
        var ids = ((PagedResponse<TaskSummaryResponse>)mentioned.Data!).Items.Select(i => i.Id).ToList();
        ids.Should().Contain(parent.Id).And.NotContain(child.Id);
        (await Detail("sales02", parent.Id)).Subtasks.Select(s => s.Id).Should().Equal(child.Id);
    }

    [Fact]
    public async Task C14_SupervisorInheritsVisibility_ButCannotReassignOthersChild()
    {
        var t = await BuildRestrictedTarget("tk2");
        var rec = await NewRecord(t, "sales01", "機密案件甲");
        var parent = await SeedTask("辦說明會", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec);
        var child = await SeedChild(parent, "外部講者", createdBy: "sales01", assignee: Outsider);

        var c = await Detail("manager01", child.Id);
        var patch = await SendAsUser("manager01", new UpdateTaskRequest(child.Id, J("""{"assigneeUserId":"sales02"}""")));

        c.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
        c.RuleKey.Should().BeNull();
        c.RuleDeleted.Should().BeFalse();
        c.ParentVisible.Should().BeTrue();
        c.CanReassign.Should().BeFalse();
        c.Supervised.Should().BeFalse();
        WireText(c).Should().NotContain("機密案件甲").And.NotContain("tk2");
        patch.IsForbidden.Should().BeTrue();
        (await Load(child.Id)).AssigneeUserId.Should().Be(Outsider);
    }

    [Fact]
    public async Task C15_SupervisorOnSubordinatesOwnChild_ReassignOnly()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: Outsider, assignee: Outsider);
        var child = await SeedChild(parent, "訂場地", createdBy: Outsider, assignee: "sales02");

        var c = await Detail("manager01", child.Id);
        var status = await SendAsUser("manager01", new UpdateTaskRequest(child.Id, J("""{"status":"done"}""")));
        var reassign = await SendAsUser("manager01", new UpdateTaskRequest(child.Id, J("""{"assigneeUserId":"sales01"}""")));

        c.Supervised.Should().BeTrue();
        c.ParentVisible.Should().BeFalse("父任務不是部屬的任務");
        status.IsForbidden.Should().BeTrue();
        reassign.IsSuccess.Should().BeTrue(reassign.Message);
    }

    [Fact]
    public async Task C16_ParentEditorCanEditChild()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales02", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "sales02");

        var r = await SendAsUser("sales01", new UpdateTaskRequest(child.Id, J("""{"title":"訂大場地"}""")));

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Load(child.Id)).Title.Should().Be("訂大場地");
        (await Detail("sales01", child.Id)).CanDelete.Should().BeFalse("父任務負責人不能刪子任務（ST-Q2）");
    }

    // ---- 刪除 ----

    [Fact]
    public async Task C35_DeleteParent_DeletesAllChildrenCommentsAndNotifications()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var done = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "sales02", status: TaskItemStatus.Done);
        var open = await SeedChild(parent, "印海報", createdBy: "sales02");
        Db.Context.Add(new TaskComment { TaskId = done.Id, Kind = TaskCommentKind.Comment, AuthorUserId = "sales02", Body = "已訂好", CreatedTime = SeedTime });
        foreach (var id in new[] { parent.Id, done.Id, open.Id })
            Db.Context.Add(new Notification
            {
                UserId = "sales01", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "x", Link = $"/tasks?open={id}",
                SourceType = "task", SourceId = id, CreatedTime = SeedTime
            });
        var other = await SeedTask("不相干", createdBy: "sales01");
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var r = await SendAsUser("sales01", new DeleteTaskRequest(parent.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Db.Context.Set<TaskItem>().AsNoTracking().Select(t => t.Id).ToListAsync()).Should().Equal(other.Id);
        (await Db.Context.Set<TaskComment>().CountAsync(c => c.TaskId != other.Id)).Should().Be(0);
        (await Db.Context.Set<Notification>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task C36_RuleParent_CannotDelete_ChildrenKept()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var ruleId = await SeedRule(t, "follow", "追蹤");
        var parent = await SeedTask("追蹤", createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: ruleId);
        var child = await SeedChild(parent, "打電話", createdBy: "sales01");

        var r = await SendAsUser("admin", new DeleteTaskRequest(parent.Id));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(TaskMessages.RuleTaskNoDelete);
        (await Db.Context.Set<TaskItem>().AnyAsync(x => x.Id == child.Id)).Should().BeTrue();
    }

    [Theory]
    [InlineData("sales02", true)]    // 子任務建立者
    [InlineData("sales01", true)]    // 父任務建立者
    [InlineData("admin", true)]      // 系統管理員
    [InlineData("sales03", false)]   // 父任務負責人（不是任一建立者）
    [InlineData("buyer01", false)]   // 子任務負責人
    public async Task C37_DeleteChild_PermissionMatrix(string who, bool allowed)
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales03");
        var child = await SeedChild(parent, "訂場地", createdBy: "sales02", assignee: "buyer01");
        await SeedChild(parent, "印海報", createdBy: "sales01");

        var r = await SendAsUser(who, new DeleteTaskRequest(child.Id));

        if (allowed)
        {
            r.IsSuccess.Should().BeTrue(r.Message);
            (await Db.Context.Set<TaskItem>().AnyAsync(x => x.Id == child.Id)).Should().BeFalse();
            var entry = (await SystemEntries(parent.Id)).Should().ContainSingle(c => c.SystemEvent == TaskSystemEvents.SubtaskDeleted).Subject;
            entry.AuthorUserId.Should().Be(who);
            Data(entry).Should().Be((child.Id, "訂場地"));
            (await Detail("sales01", parent.Id)).Progress!.Total.Should().Be(1);
        }
        else
        {
            r.IsForbidden.Should().BeTrue();
            (await Db.Context.Set<TaskItem>().AnyAsync(x => x.Id == child.Id)).Should().BeTrue();
            (await Detail(who, child.Id)).CanDelete.Should().BeFalse();
        }
    }

    // ---- 記錄頁與進度 ----

    [Fact]
    public async Task C41_RecordTasks_TopLevelOnly_ParentRowHasProgress()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var parent = await SeedTask("辦說明會", createdBy: "sales01", target: t, recordId: rec);
        var standalone = await SeedTask("寄資料", createdBy: "sales01", target: t, recordId: rec);
        await SeedChild(parent, "訂場地", createdBy: "sales01", status: TaskItemStatus.Done);
        await SeedChild(parent, "印海報", createdBy: "sales01");

        var r = await SendAsUser("sales01", new GetRecordTasksRequest(rec));

        r.IsSuccess.Should().BeTrue(r.Message);
        var items = ((RecordTasksResponse)r.Data!).Items;
        items.Select(i => i.Id).Should().BeEquivalentTo([parent.Id, standalone.Id]);
        var p = items.Single(i => i.Id == parent.Id);
        p.Level.Should().Be(TaskLevel.Parent);
        p.Progress.Should().Be(new TaskProgress(1, 0, 2));
        var s = items.Single(i => i.Id == standalone.Id);
        s.Level.Should().Be(TaskLevel.Standalone);
        s.Progress.Should().BeNull();
    }

    [Fact]
    public async Task C42_Progress_CountsDoneCancelledTotal()
    {
        var mixed = await SeedTask("五個子任務", createdBy: "sales01");
        foreach (var (title, status) in new[]
                 {
                     ("一", TaskItemStatus.Done), ("二", TaskItemStatus.Done), ("三", TaskItemStatus.Cancelled),
                     ("四", TaskItemStatus.Todo), ("五", TaskItemStatus.InProgress)
                 })
            await SeedChild(mixed, title, createdBy: "sales01", status: status);
        var allCancelled = await SeedTask("全取消", createdBy: "sales01");
        await SeedChild(allCancelled, "甲", createdBy: "sales01", status: TaskItemStatus.Cancelled);
        await SeedChild(allCancelled, "乙", createdBy: "sales01", status: TaskItemStatus.Cancelled);
        var none = await SeedTask("沒有子任務", createdBy: "sales01");

        var m = await Detail("sales01", mixed.Id);
        var c = await Detail("sales01", allCancelled.Id);
        var n = await Detail("sales01", none.Id);
        var list = await SendAsUser("sales01", new GetMyTasksRequest("created", "all", 1, 50));

        m.Progress.Should().Be(new TaskProgress(2, 1, 5));
        m.Subtasks.Select(s => s.Title).Should().Equal("一", "二", "三", "四", "五");
        c.Progress.Should().Be(new TaskProgress(0, 2, 2));
        c.Level.Should().Be(TaskLevel.Parent);
        n.Progress.Should().BeNull();
        n.Level.Should().Be(TaskLevel.Standalone);
        var rows = ((PagedResponse<TaskSummaryResponse>)list.Data!).Items;
        rows.Single(x => x.Id == mixed.Id).Progress.Should().Be(new TaskProgress(2, 1, 5));
        rows.Where(x => x.ParentTaskId == mixed.Id).Should().HaveCount(5).And.OnlyContain(x => x.ParentTitle == "五個子任務" && x.Level == TaskLevel.Child);
    }

    [Fact]
    public async Task OrphanChild_TreatedAsStandaloneParentInfo()
    {
        var parent = await SeedTask("已不存在的父任務", createdBy: "sales01");
        var child = await SeedChild(parent, "孤兒", createdBy: "sales01");
        await Db.Context.Set<TaskItem>().Where(t => t.Id == parent.Id).ExecuteDeleteAsync();

        var c = await Detail("sales01", child.Id);

        c.ParentTaskId.Should().Be(parent.Id);
        c.ParentTitle.Should().BeNull();
        c.ParentVisible.Should().BeFalse();
    }
}
