using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務事件寫站內通知（設計 §6.1）：誰收到、誰不收到。</summary>
public class TaskNotificationTests : TaskTestBase
{
    private async Task<List<Notification>> Inbox(string userId)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.Id).ToListAsync();
    }

    private async Task<long> CreateTask(string title, string? assignee)
    {
        var r = await Send(new CreateTaskRequest(new TaskInput(title, null, assignee, null)));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return ((TaskDetailResponse)r.Data!).Id;
    }

    private async Task Patch(long id, string json)
    {
        var r = await Send(new UpdateTaskRequest(id, J(json)));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    private async Task Comment(long id, string body)
    {
        var r = await Send(new AddTaskCommentRequest(id, body, []));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Assign_NotifiesNewAssignee_NotSelf()
    {
        As("admin");
        await CreateTask("整理報價", "sales01");
        (await Inbox("sales01")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskAssigned);

        As("sales01");
        var own = await CreateTask("自己的待辦", "sales01");
        await Patch(own, """{"assigneeUserId":null}""");
        await Patch(own, """{"assigneeUserId":"sales01"}""");

        (await Inbox("sales01")).Should().ContainSingle("指派給自己不通知");
        (await Inbox("admin")).Should().BeEmpty();
    }

    [Fact]
    public async Task Mention_NotifiesMentioned_NotSelf()
    {
        var task = await SeedTask(createdBy: "sales01");

        As("sales01");
        await Comment(task.Id, "@{sales02} 與我自己 @{sales01}");

        (await Inbox("sales02")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskMentioned);
        (await Inbox("sales01")).Should().BeEmpty("自己 @ 自己、自己留言都不通知自己");
    }

    [Fact]
    public async Task Comment_NotifiesAssigneeAndCreator_ExceptAuthorAndMentioned()
    {
        var task = await SeedTask(createdBy: "admin", assignee: "sales01", mentioned: ["sales02"]);

        As("sales02");
        await Comment(task.Id, "@{sales01} 請看");

        (await Inbox("sales01")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskMentioned);
        (await Inbox("admin")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCommented);
        (await Inbox("sales02")).Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteOrCancel_NotifiesCreatorWhenNotSelf()
    {
        var task = await SeedTask(createdBy: "admin", assignee: "sales01");

        As("sales01");
        await Patch(task.Id, """{"status":"done"}""");
        (await Inbox("admin")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCompleted);

        As("admin");
        await Patch(task.Id, """{"status":"cancelled"}""");
        (await Inbox("admin")).Should().ContainSingle("建立者自己取消不通知");
        (await Inbox("sales01")).Should().BeEmpty("完成、取消只通知建立者");
    }

    private async Task Override(Target t, string code, string jsonValue)
    {
        As("admin");
        (await Send(new UpdateModulePolicyRequest(t.Key, code, J(jsonValue)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Comment_RecipientsFollowModulePolicy()
    {
        var t = await BuildTarget();
        await Override(t, "task.notify.commented", """["creator"]""");
        var task = await SeedTask(createdBy: "admin", assignee: "sales01", mentioned: ["sales02"], moduleId: t.ModuleId);

        As("sales02");
        await Comment(task.Id, "請看");

        (await Inbox("admin")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCommented);
        (await Inbox("sales01")).Should().BeEmpty("政策只通知建立者");
    }

    [Fact]
    public async Task Comment_EmptyRoles_NobodyGetsCommented_MentionsStillSent()
    {
        var t = await BuildTarget();
        await Override(t, "task.notify.commented", "[]");
        var task = await SeedTask(createdBy: "admin", assignee: "sales01", mentioned: ["sales02"], moduleId: t.ModuleId);

        As("sales02");
        await Comment(task.Id, "@{sales01} 請看");

        (await Inbox("sales01")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskMentioned);
        (await Inbox("admin")).Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_NotifiesAssigneeToo_WhenPolicySaysSo()
    {
        var t = await BuildTarget();
        await Override(t, "task.notify.completed", """["assignee","creator"]""");
        var task = await SeedTask(createdBy: "sales02", assignee: "sales01", moduleId: t.ModuleId);

        As("admin");
        await Patch(task.Id, """{"status":"done"}""");

        (await Inbox("sales01")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCompleted);
        (await Inbox("sales02")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCompleted);
    }

    [Fact]
    public async Task Complete_SameAssigneeAndCreator_NotifiedOnce()
    {
        var t = await BuildTarget();
        await Override(t, "task.notify.completed", """["assignee","creator"]""");
        var task = await SeedTask(createdBy: "sales01", assignee: "sales01", moduleId: t.ModuleId);

        As("admin");
        await Patch(task.Id, """{"status":"done"}""");

        (await Inbox("sales01")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCompleted);
    }

    [Fact]
    public async Task Complete_ActorIsAssignee_NotNotified_CreatorIs()
    {
        var t = await BuildTarget();
        await Override(t, "task.notify.completed", """["assignee","creator"]""");
        var task = await SeedTask(createdBy: "admin", assignee: "sales01", moduleId: t.ModuleId);

        As("sales01");
        await Patch(task.Id, """{"status":"done"}""");

        (await Inbox("sales01")).Should().BeEmpty("完成的人本人不通知");
        (await Inbox("admin")).Select(n => n.Kind).Should().Equal(NotificationKinds.TaskCompleted);
    }

    [Fact]
    public async Task CompleteOrCancel_SystemCreator_NoNotification()
    {
        var task = await SeedTask(createdBy: null, assignee: "sales01");

        As("sales01");
        await Patch(task.Id, """{"status":"done"}""");

        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MentionInactiveUser_NoNotification()
    {
        var task = await SeedTask(createdBy: "sales01");
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "maint01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        As("sales01");
        await Comment(task.Id, "@{maint01} @{ghost} @{");

        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Notification_HasLinkAndSnapshot()
    {
        As("admin");
        var id = await CreateTask("寄送合約", "sales01");

        var n = (await Inbox("sales01")).Single();

        n.Link.Should().Be($"/tasks?open={id}");
        n.TitleSnapshot.Should().Be("寄送合約");
        n.SourceType.Should().Be("task");
        n.SourceId.Should().Be(id);
        n.ReadTime.Should().BeNull();
        n.CreatedTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
    }

    [Fact]
    public async Task DeleteTask_RemovesItsNotificationsOnly()
    {
        As("admin");
        var gone = await CreateTask("要刪的", "sales01");
        var kept = await CreateTask("留著的", "sales01");
        Db.Context.Add(new Notification
        {
            UserId = "sales01", Kind = "other", TitleSnapshot = "別的來源", Link = "/x", SourceType = "approval", SourceId = gone,
            CreatedTime = SeedTime
        });
        await Db.Context.SaveChangesAsync();

        (await Send(new DeleteTaskRequest(gone))).IsSuccess.Should().BeTrue();

        (await Inbox("sales01")).Select(n => n.SourceId).Should().Equal(kept, gone);
        (await Inbox("sales01")).Single(n => n.SourceId == gone).SourceType.Should().Be("approval");
    }
}
