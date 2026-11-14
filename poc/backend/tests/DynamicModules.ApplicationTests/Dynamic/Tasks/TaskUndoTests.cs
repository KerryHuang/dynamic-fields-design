using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>主管把部屬的任務重派到範圍外：回一次性的復原憑證，只能由本人在 2 分鐘內、任務沒被改過時使用（看板設計 §5.4、T24）。</summary>
public class TaskUndoTests : TaskTestBase
{
    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override bool ScopePerRequest => true;

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
    }

    private async Task<ServiceResult> Reassign(string user, long id, string? to)
    {
        As(user);
        var r = await Send(new UpdateTaskRequest(id, J(JsonSerializer.Serialize(new { assigneeUserId = to }))));
        As("admin");
        return r;
    }

    private async Task<ServiceResult> Undo(string user, long id, string token)
    {
        As(user);
        var r = await Send(new UndoReassignRequest(id, token));
        As("admin");
        return r;
    }

    /// <summary>ops01 把倉管人員的任務派給業務一（不是他的部屬）→ 看不到了，拿到憑證。</summary>
    private async Task<(long Id, string Token)> MoveOut()
    {
        var task = await SeedTask("盤點備品倉", createdBy: "keeper01", assignee: "keeper01");
        var r = await Reassign("ops01", task.Id, "sales01");
        r.IsSuccess.Should().BeTrue(r.Message);
        var hidden = r.Data.Should().BeOfType<TaskHiddenAfterWrite>().Subject;
        return (task.Id, hidden.UndoToken!);
    }

    [Fact]
    public async Task ReassignToNonSubordinate_VisibleFalse_TokenIssued_NoTaskContent()
    {
        var task = await SeedTask("盤點備品倉", createdBy: "keeper01", assignee: "keeper01");

        var r = await Reassign("ops01", task.Id, "sales01");

        var json = JsonSerializer.SerializeToElement(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "visible", "undoToken");
        json.GetProperty("visible").GetBoolean().Should().BeFalse();
        (await Load(task.Id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_RestoresOriginalAssignee()
    {
        var (id, token) = await MoveOut();

        var r = await Undo("ops01", id, token);

        r.IsSuccess.Should().BeTrue(r.Message);
        ((TaskDetailResponse)r.Data!).AssigneeUserId.Should().Be("keeper01");
        (await Load(id)).AssigneeUserId.Should().Be("keeper01");
        (await Db.Context.Set<TaskComment>().CountAsync(c => c.TaskId == id && c.SystemEvent == TaskSystemEvents.AssigneeChanged)).Should().Be(2);
    }

    [Fact]
    public async Task Undo_Twice_SecondRejected()
    {
        var (id, token) = await MoveOut();
        _clock.Now = _clock.Now.AddSeconds(5);
        (await Undo("ops01", id, token)).IsSuccess.Should().BeTrue();
        _clock.Now = _clock.Now.AddSeconds(5);

        var second = await Undo("ops01", id, token);

        second.IsInvalid.Should().BeTrue();
        second.Errors.Values.Single()[0].Should().Be(TaskMessages.UndoInvalid);
    }

    [Fact]
    public async Task Undo_ByAnotherUser_400()
    {
        var (id, token) = await MoveOut();

        (await Undo("manager01", id, token)).IsInvalid.Should().BeTrue();
        (await Load(id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_AfterOtherChange_400()
    {
        var (id, token) = await MoveOut();
        _clock.Now = _clock.Now.AddSeconds(30);
        As("admin");
        (await Send(new UpdateTaskRequest(id, J("""{"title":"改過了"}""")))).IsSuccess.Should().BeTrue();

        (await Undo("ops01", id, token)).IsInvalid.Should().BeTrue();
        (await Load(id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_Expired_400()
    {
        var (id, token) = await MoveOut();
        _clock.Now = _clock.Now.AddMinutes(2).AddSeconds(1);

        (await Undo("ops01", id, token)).IsInvalid.Should().BeTrue();
        (await Load(id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_TamperedToken_400()
    {
        var (id, token) = await MoveOut();

        (await Undo("ops01", id, token + "x")).IsInvalid.Should().BeTrue();
        (await Undo("ops01", id, "not-a-token")).IsInvalid.Should().BeTrue();
        (await Undo("ops01", id, "")).IsInvalid.Should().BeTrue();
        (await Load(id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_TokenOfAnotherTask_400()
    {
        var (_, token) = await MoveOut();
        var other = await SeedTask("別的任務", createdBy: "keeper01", assignee: "sales01");

        (await Undo("ops01", other.Id, token)).IsInvalid.Should().BeTrue();
        (await Load(other.Id)).AssigneeUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task Undo_TaskDeleted_404()
    {
        var (id, token) = await MoveOut();
        As("admin");
        (await Send(new DeleteTaskRequest(id))).IsSuccess.Should().BeTrue();

        (await Undo("ops01", id, token)).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_FrozenModule_409()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var task = await SeedTask("掛記錄", createdBy: "keeper01", assignee: "keeper01", target: t, recordId: rec);
        var r = await Reassign("ops01", task.Id, "sales01");
        var token = r.Data.Should().BeOfType<TaskHiddenAfterWrite>().Subject.UndoToken!;
        await Db.Context.Set<Module>().Where(m => m.Key == t.Key).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();   // ExecuteUpdate 不更新追蹤器，不清會讀到舊的模組狀態

        (await Undo("ops01", task.Id, token)).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Reassign_StillVisible_NoToken()
    {
        var task = await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");

        var r = await Reassign("manager01", task.Id, "sales02");

        r.Data.Should().BeOfType<TaskDetailResponse>();
    }
}
