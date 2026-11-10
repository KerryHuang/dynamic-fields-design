using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

public class TaskCommentTests : TaskTestBase
{
    private sealed class RecordingNotifier : ITaskNotifier
    {
        public List<TaskChange> Changes { get; } = [];

        public Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }

    private readonly RecordingNotifier _notifier = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ITaskNotifier>(_notifier);

    private Task<ServiceResult> Comment(long taskId, string body) =>
        Send(new AddTaskCommentRequest(taskId, body, []));

    private async Task<TaskCommentResponse> MustComment(long taskId, string body)
    {
        var r = await Comment(taskId, body);
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (TaskCommentResponse)r.Data!;
    }

    private async Task<List<TaskCommentResponse>> Comments(long taskId)
    {
        var r = await Send(new GetTaskCommentsRequest(taskId));
        r.IsSuccess.Should().BeTrue(r.Message);
        return ((IReadOnlyList<TaskCommentResponse>)r.Data!).ToList();
    }

    private async Task<TaskPermissions> Eval(string userId, long taskId)
    {
        var task = await Load(taskId);
        As(userId);
        var p = await Svc<TaskAccess>().EvaluateAsync(task, default);
        As("admin");
        return p;
    }

    private async Task Deactivate(string userId) =>
        await Db.Context.Set<AppUser>().Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

    [Fact]
    public void TaskMentions_Parse()
    {
        TaskMentions.UserIds("嗨 @{sales01} 與 @{sales02}，@{sales01}").Should().Equal("sales01", "sales02");
        TaskMentions.UserIds("@{} @{a b} @sales01 @{x").Should().BeEmpty();
        TaskMentions.UserIds("@{" + new string('a', 51) + "}").Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_MentionAddsParticipant_MentionedSeesTaskOnly()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "sales01", assignee: "sales02", target: t, recordId: rec);

        As("sales01");
        var created = await MustComment(task.Id, "請 @{floor01} 確認");

        created.Kind.Should().Be(TaskCommentKind.Comment);
        created.AuthorUserId.Should().Be("sales01");
        created.AuthorName.Should().Be("業務一");
        created.Body.Should().Be("請 @{floor01} 確認");
        created.Mentions.Should().Equal(new MentionRef("floor01", "現場人員", true));
        created.Attachments.Should().BeEmpty();
        created.CanWithdraw.Should().BeTrue();
        (await Load(task.Id)).Participants.Should().ContainSingle(p => p.UserId == "floor01" && p.Reason == TaskParticipantReason.Mentioned);
        var floor = await Eval("floor01", task.Id);
        floor.CanSee.Should().BeTrue();
        floor.Record!.Accessible.Should().BeFalse();

        _notifier.Changes.Should().ContainSingle(c => c.Kind == TaskChangeKind.Mentioned && c.UserId == "floor01" && c.ActorUserId == "sales01");
        _notifier.Changes.Single(c => c.Kind == TaskChangeKind.Commented).Mentioned.Should().Equal("floor01");

        As("floor01");
        var list = await Comments(task.Id);
        list.Select(c => c.Kind).Should().Equal(TaskCommentKind.Comment);   // SeedTask 沒有系統記錄
        list[0].CanWithdraw.Should().BeFalse();
    }

    [Fact]
    public async Task Comment_MentionUnknownOrInactive_NoParticipant()
    {
        var task = await SeedTask(createdBy: "sales01");
        await Deactivate("maint01");

        As("sales01");
        var created = await MustComment(task.Id, "@{ghost} 與 @{maint01}");

        created.Mentions.Should().Equal(new MentionRef("maint01", "保養人員", false));
        (await Load(task.Id)).Participants.Select(p => p.UserId).Should().Equal("sales01");
        _notifier.Changes.Should().NotContain(c => c.Kind == TaskChangeKind.Mentioned);
        _notifier.Changes.Single(c => c.Kind == TaskChangeKind.Commented).Mentioned.Should().BeEmpty();
    }

    [Fact]
    public async Task Comment_BlankOr4001_400()
    {
        var task = await SeedTask();

        Err(await Comment(task.Id, "  "), "body").Should().Be("留言不能是空的");
        Err(await Comment(task.Id, new string('x', 4001)), "body").Should().Be("留言不能超過 4000 字");
        (await Comment(task.Id, new string('x', 4000))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Comment_InvisibleTask_404()
    {
        var task = await SeedTask(createdBy: "sales01");

        As("floor01");
        var r = await Comment(task.Id, "hi");

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("找不到任務");
        (await Send(new GetTaskCommentsRequest(task.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Withdraw_ByAuthorOrAdmin_HidesBody_OthersForbidden()
    {
        var task = await SeedTask(createdBy: "sales01", mentioned: ["floor01"]);
        As("sales01");
        var first = await MustComment(task.Id, "第一則 @{floor01}");
        var second = await MustComment(task.Id, "第二則");

        As("floor01");
        (await Send(new WithdrawTaskCommentRequest(task.Id, first.Id))).IsForbidden.Should().BeTrue();
        As("sales01");
        var withdrawn = await Send(new WithdrawTaskCommentRequest(task.Id, first.Id));
        withdrawn.IsSuccess.Should().BeTrue(withdrawn.Message);
        As("admin");
        (await Send(new WithdrawTaskCommentRequest(task.Id, second.Id))).IsSuccess.Should().BeTrue();
        (await Send(new WithdrawTaskCommentRequest(task.Id, second.Id + 1000))).Message.Should().Be("找不到留言");

        As("floor01");
        var list = await Comments(task.Id);
        list.Should().HaveCount(2);
        list.Should().OnlyContain(c => c.Withdrawn && c.Body == null && c.Mentions.Count == 0 && !c.CanWithdraw);
        var row = await Db.Context.Set<TaskComment>().AsNoTracking().SingleAsync(c => c.Id == first.Id);
        row.WithdrawnBy.Should().Be("sales01");
        row.WithdrawnTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
    }

    [Fact]
    public async Task Withdraw_SystemEntry_409()
    {
        var created = (TaskDetailResponse)(await Send(new CreateTaskRequest(new TaskInput("準備資料", null, null, null)))).Data!;
        var entry = (await Comments(created.Id)).Single();
        entry.Kind.Should().Be(TaskCommentKind.System);
        entry.SystemEvent.Should().Be(TaskSystemEvents.Created);
        entry.CanWithdraw.Should().BeFalse();

        var r = await Send(new WithdrawTaskCommentRequest(created.Id, entry.Id));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("系統記錄不能撤回");
    }

    [Fact]
    public async Task Comment_OnFrozenTask_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask(createdBy: "admin", target: t, recordId: rec);
        var comment = await MustComment(task.Id, "先留一則");
        await Db.Context.Set<Module>().Where(m => m.Id == t.ModuleId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();   // 測試共用同一個 DbContext：丟掉留言時追蹤到的模組

        var r = await Comment(task.Id, "hi");

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("應用程式已關閉，資料唯讀");
        (await Send(new WithdrawTaskCommentRequest(task.Id, comment.Id))).IsConflict.Should().BeTrue();
        (await Comments(task.Id)).Single().CanWithdraw.Should().BeFalse();
    }

    [Fact]
    public async Task MentionCandidates_ActiveOnly_Filtered_Max20()
    {
        for (var i = 1; i <= 25; i++)
            Db.Context.Add(new AppUser { UserId = $"temp{i:00}", DisplayName = $"臨時人員{i:00}" });
        await Db.Context.SaveChangesAsync();
        await Deactivate("maint01");
        As("floor01");

        async Task<List<string>> Ids(string? keyword) =>
            ((IReadOnlyList<UserResponse>)(await Send(new GetMentionCandidatesRequest(keyword))).Data!).Select(u => u.UserId).ToList();

        (await Ids("TEMP")).Should().HaveCount(20);
        (await Ids("SALES")).Should().Equal("sales01", "sales02");
        (await Ids("業務")).Should().BeEquivalentTo("sales01", "sales02", "manager01");
        (await Ids("保養")).Should().BeEmpty();
        (await Ids(null)).Should().HaveCount(20).And.NotContain("maint01");
    }
}
