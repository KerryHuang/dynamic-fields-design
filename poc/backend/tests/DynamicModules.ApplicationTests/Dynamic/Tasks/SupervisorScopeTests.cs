using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>主管範圍：看得到並重派部屬的任務，只能改負責人（看板設計 §4）。示範組織：manager01 管總經理室與業務部（遞迴＝全公司），ops01 管營運部。</summary>
public class SupervisorScopeTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<ServiceResult> Patch(string user, long id, string json)
    {
        As(user);
        var r = await Send(new UpdateTaskRequest(id, J(json)));
        As("admin");
        return r;
    }

    private async Task SetPolicy(PolicyDefinition def, string value)
    {
        Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
    }

    private async Task<TaskPage> List(string user, string tab, string filter = "open")
    {
        As(user);
        var r = await Send(new GetMyTasksRequest(tab, filter, 1, 50));
        r.IsSuccess.Should().BeTrue(r.Message);
        As("admin");
        return new TaskPage(((PagedResponse<TaskSummaryResponse>)r.Data!).Items.ToList(), ((PagedResponse<TaskSummaryResponse>)r.Data!).TotalCount);
    }

    private sealed record TaskPage(List<TaskSummaryResponse> Items, int Total)
    {
        public HashSet<long> Ids => Items.Select(i => i.Id).ToHashSet();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
    }

    [Fact]
    public async Task Reassign_Allowed_StatusChange403()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var ok = await Patch("manager01", task.Id, """{"assigneeUserId":"sales02"}""");
        var status = await Patch("manager01", task.Id, """{"status":"done"}""");

        ok.IsSuccess.Should().BeTrue(ok.Message);
        var detail = (TaskDetailResponse)ok.Data!;
        detail.AssigneeUserId.Should().Be("sales02");
        detail.CanEdit.Should().BeFalse();
        detail.CanReassign.Should().BeTrue();
        detail.Supervised.Should().BeTrue();
        status.IsForbidden.Should().BeTrue();
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.Todo);
    }

    [Fact]
    public async Task Reassign_WithOtherFields_403_NothingWritten()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var r = await Patch("manager01", task.Id, """{"assigneeUserId":"sales02","title":"改標題"}""");

        r.IsForbidden.Should().BeTrue();
        r.Message.Should().Be(TaskMessages.SupervisorOnlyAssignee);
        var after = await Load(task.Id);
        after.AssigneeUserId.Should().Be("sales01");
        after.Title.Should().Be("整理名片");
        (await Db.Context.Set<TaskComment>().CountAsync(c => c.TaskId == task.Id && c.SystemEvent == TaskSystemEvents.AssigneeChanged)).Should().Be(0);
    }

    [Fact]
    public async Task Reassign_NewAssigneeNotified_OldStaysParticipant()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        (await Patch("manager01", task.Id, """{"assigneeUserId":"sales02"}""")).IsSuccess.Should().BeTrue();

        var after = await Load(task.Id);
        after.Participants.Should().Contain(p => p.UserId == "sales01" && p.Reason == TaskParticipantReason.Assignee);
        after.Participants.Should().Contain(p => p.UserId == "sales02" && p.Reason == TaskParticipantReason.Assignee);
        after.Participants.Should().NotContain(p => p.UserId == "manager01");
        (await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == "sales02").ToListAsync())
            .Should().ContainSingle(n => n.Kind == NotificationKinds.TaskAssigned);
    }

    [Fact]
    public async Task Reassign_RuleTask_SetsAssigneeOverridden()
    {
        var task = await SeedTask("規則任務", createdBy: null, assignee: "sales01", ruleId: 7);

        (await Patch("manager01", task.Id, """{"assigneeUserId":"sales02"}""")).IsSuccess.Should().BeTrue();

        (await Load(task.Id)).AssigneeOverridden.Should().BeTrue();
    }

    [Fact]
    public async Task Reassign_FrozenModule_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask("掛記錄", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Key == t.Key).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));

        var r = await Patch("manager01", task.Id, """{"assigneeUserId":"sales02"}""");

        r.IsConflict.Should().BeTrue();
        (await Load(task.Id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task FrozenSubordinateTask_NotSupervised()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask("掛記錄", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec);
        await Db.Context.Set<Module>().Where(m => m.Key == t.Key).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        As("manager01");
        var r = await Send(new GetTaskRequest(task.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        var detail = (TaskDetailResponse)r.Data!;
        detail.Frozen.Should().BeTrue();
        detail.CanReassign.Should().BeFalse();
        detail.Supervised.Should().BeFalse("凍結時不能重派，不該顯示主管說明");
    }

    [Fact]
    public async Task Reassign_ToSelf_TakesOver_CanEditAfterwards()
    {
        // 主管把部屬任務派給自己＝接手（裁決 S2）：之後以負責人身分可改全部欄位
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var take = await Patch("manager01", task.Id, """{"assigneeUserId":"manager01"}""");
        var status = await Patch("manager01", task.Id, """{"status":"done"}""");

        take.IsSuccess.Should().BeTrue(take.Message);
        var detail = (TaskDetailResponse)take.Data!;
        detail.AssigneeUserId.Should().Be("manager01");
        detail.CanEdit.Should().BeTrue();
        detail.Supervised.Should().BeFalse();
        status.IsSuccess.Should().BeTrue(status.Message);
        (await Load(task.Id)).Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public async Task SameScope_OrgChanged_ResetRefreshesSubordinates()
    {
        // Demo 產生器整段共用一個 scope：每一步之間的重設要讓部屬名單重算（S4）
        As("ops01");
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var visibility = sp.GetRequiredService<TaskVisibilityService>();
        (await visibility.SubordinatesAsync(default)).Should().Contain("keeper01");
        (await visibility.ForCurrentUserAsync(default)).Subordinates.Should().Contain("keeper01");

        await Db.Context.Set<Department>().Where(d => d.ManagerUserId == "ops01")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerUserId, (string?)null));
        Db.Context.ChangeTracker.Clear();
        DemoStructure.ResetPerRequestState(sp.GetRequiredService<IWriteJournal>(), sp.GetRequiredService<EffectBudget>(),
            sp.GetRequiredService<ApprovalWriteContext>(), sp.GetRequiredService<ApprovalLockService>(),
            sp.GetRequiredService<AttachmentUploadTracker>(), visibility);

        (await visibility.SubordinatesAsync(default)).Should().BeEmpty();
        (await visibility.ForCurrentUserAsync(default)).Subordinates.Should().BeEmpty();
        As("admin");
    }

    [Fact]
    public async Task UnassignedCreatedBySubordinate_Visible()
    {
        var task = await SeedTask("整理樣品櫃", createdBy: "sales02", assignee: null);

        As("manager01");
        var r = await Send(new GetTaskRequest(task.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        ((TaskDetailResponse)r.Data!).Supervised.Should().BeTrue();
    }

    [Fact]
    public async Task SubordinateOnlyCreator_NotVisible()
    {
        // 建立者 keeper01 是 ops01 的部屬，但負責人 sales01 不是：不算部屬的任務
        var task = await SeedTask("交辦", createdBy: "keeper01", assignee: "sales01");

        As("ops01");
        (await Send(new GetTaskRequest(task.Id))).IsNotFound.Should().BeTrue();
        (await List("ops01", "subordinates", "all")).Ids.Should().NotContain(task.Id);
    }

    [Fact]
    public async Task SubordinatesTab_MatchesDefinition_AndFilters()
    {
        var today = new DateOnly(2026, 11, 4);
        var overdue = await SeedTask("逾期", createdBy: "sales01", assignee: "sales01", due: today.AddDays(-2));
        var todayTask = await SeedTask("今天", createdBy: "sales01", assignee: "sales01", due: today);
        var unassigned = await SeedTask("未指派", createdBy: "sales02", assignee: null, due: today.AddDays(5));
        var done = await SeedTask("已完成", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Done);
        var keeper = await SeedTask("倉管", createdBy: "keeper01", assignee: "keeper01");
        var own = await SeedTask("自己的", createdBy: "manager01", assignee: "manager01");
        var outsider = await SeedTask("管理員自己的", createdBy: "admin", assignee: "admin");

        var open = await List("manager01", "subordinates");
        var all = await List("manager01", "subordinates", "all");
        var overdueOnly = await List("manager01", "subordinates", "overdue");
        var todayOnly = await List("manager01", "subordinates", "today");
        var ops = await List("ops01", "subordinates");

        open.Ids.Should().BeEquivalentTo(new[] { overdue.Id, todayTask.Id, unassigned.Id, keeper.Id });
        all.Ids.Should().BeEquivalentTo(new[] { overdue.Id, todayTask.Id, unassigned.Id, done.Id, keeper.Id });
        all.Ids.Should().NotContain([own.Id, outsider.Id]);
        overdueOnly.Ids.Should().BeEquivalentTo(new[] { overdue.Id });
        todayOnly.Ids.Should().BeEquivalentTo(new[] { todayTask.Id });
        all.Total.Should().Be(5);
        ops.Ids.Should().BeEquivalentTo(new[] { keeper.Id });
    }

    [Fact]
    public async Task SubordinatesTab_PolicyOff_Empty()
    {
        await SeedTask("業務一的", createdBy: "sales01", assignee: "sales01");
        await SetPolicy(PolicyCatalog.TaskSupervisorScope, "false");

        var page = await List("manager01", "subordinates", "all");

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task Capabilities_TrueOnlyWithSubordinates()
    {
        async Task<bool> Subordinates(string user)
        {
            As(user);
            var r = await Send(new GetTaskCapabilitiesRequest());
            As("admin");
            return ((TaskCapabilitiesResponse)r.Data!).Subordinates;
        }

        (await Subordinates("manager01")).Should().BeTrue();
        (await Subordinates("sales01")).Should().BeFalse();
        await SetPolicy(PolicyCatalog.TaskSupervisorScope, "false");
        (await Subordinates("manager01")).Should().BeFalse();
    }
}
