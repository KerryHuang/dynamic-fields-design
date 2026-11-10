using System.Text.Json;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>t2、t3a 留下的收尾：凍結不提醒、上傳檢查不留舊值、提醒失敗不影響清單、前任負責人名稱。</summary>
public class TaskFollowUpTests : TaskTestBase
{
    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private async Task<NotificationListResponse> Get(string userId)
    {
        As(userId);
        var r = await Send(new GetNotificationsRequest());
        r.IsSuccess.Should().BeTrue(r.Message);
        Db.Context.ChangeTracker.Clear();
        return (NotificationListResponse)r.Data!;
    }

    private async Task SetModuleStatus(long moduleId, ModuleStatus status)
    {
        await Db.Context.Set<Module>().Where(m => m.Id == moduleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status));
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Reminders_FrozenModule_Skipped_ResumeWhenReopened()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        await SeedTask(title: "掛記錄明天交", assignee: "sales01", target: t, recordId: rec, due: D(11, 5));
        await SeedTask(title: "掛記錄已逾期", assignee: "sales01", target: t, recordId: rec, due: D(11, 3));
        await SeedTask(title: "個人明天交", assignee: "sales01", due: D(11, 5));

        await SetModuleStatus(t.ModuleId, ModuleStatus.Closed);
        var frozen = await Get("sales01");
        frozen.Items.Select(i => i.Title).Should().Equal(new[] { "個人明天交" }, "凍結期間只剩個人任務的提醒");

        await SetModuleStatus(t.ModuleId, ModuleStatus.Active);
        var reopened = await Get("sales01");
        reopened.Items.Select(i => i.Title).Should().BeEquivalentTo(["個人明天交", "掛記錄明天交", "掛記錄已逾期"], "重新開啟後恢復提醒");
    }

    [Fact]
    public async Task UploadCheck_DoesNotKeepStaleTask_ForTheHandlerThatFollows()
    {
        var task = await SeedTask(createdBy: "admin", assignee: "sales01");

        As("sales01");
        (await Send(new CheckTaskCommentUploadRequest(task.Id))).IsSuccess.Should().BeTrue();
        // 檢查之後、處理器之前，另一個請求把負責人換成 sales02（繞過追蹤器，模擬另一條連線）
        await Db.Context.Set<TaskItem>().Where(x => x.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AssigneeUserId, "sales02"));
        var r = await Send(new AddTaskCommentRequest(task.Id, "看一下", []));
        r.IsCreated.Should().BeTrue(r.Message);

        Db.Context.ChangeTracker.Clear();
        var inbox = await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == "sales02").ToListAsync();
        inbox.Select(n => n.Kind).Should().Equal(new[] { NotificationKinds.TaskCommented }, "handler 要讀到最新的負責人");
    }

    [Fact]
    public async Task AssigneeChanged_Response_CarriesPreviousAssigneeNameAndActiveFlag()
    {
        var created = (TaskDetailResponse)(await Send(new CreateTaskRequest(new TaskInput("交接", null, "sales01", null)))).Data!;
        (await Send(new UpdateTaskRequest(created.Id, J("""{"assigneeUserId":"sales02"}""")))).IsSuccess.Should().BeTrue();
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "sales01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();

        var list = (IReadOnlyList<TaskCommentResponse>)(await Send(new GetTaskCommentsRequest(created.Id))).Data!;

        var data = list.Single(c => c.SystemEvent == TaskSystemEvents.AssigneeChanged).SystemData!.Value;
        data.GetProperty("from").GetString().Should().Be("sales01");
        data.GetProperty("to").GetString().Should().Be("sales02");
        data.GetProperty("fromName").GetString().Should().Be("業務一");
        data.GetProperty("fromActive").GetBoolean().Should().BeFalse("前任已停用，前端顯示「名稱（已停用）」");
        data.GetProperty("toName").GetString().Should().Be("業務二");
        data.GetProperty("toActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AssigneeChanged_FromNullOrUnknownUser_OmitsNameFields()
    {
        var created = (TaskDetailResponse)(await Send(new CreateTaskRequest(new TaskInput("交接", null, null, null)))).Data!;
        (await Send(new UpdateTaskRequest(created.Id, J("""{"assigneeUserId":"sales02"}""")))).IsSuccess.Should().BeTrue();

        var list = (IReadOnlyList<TaskCommentResponse>)(await Send(new GetTaskCommentsRequest(created.Id))).Data!;

        var data = list.Single(c => c.SystemEvent == TaskSystemEvents.AssigneeChanged).SystemData!.Value;
        data.TryGetProperty("fromName", out _).Should().BeFalse();
        data.GetProperty("toName").GetString().Should().Be("業務二");
    }

    [Fact]
    public async Task Upkeep_RejudgesInsideTransaction_NoDuplicateWhenAlreadyNotified()
    {
        var soon = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        As("sales01");

        // 唯讀檢查之後、交易之前，另一個請求已經補過提醒：交易內再判斷一次，不能重複寫
        (await Send(new EnsureNotificationUpkeepRequest())).IsSuccess.Should().BeTrue();
        (await Send(new EnsureNotificationUpkeepRequest())).IsSuccess.Should().BeTrue();

        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == "sales01")).Should().Be(1);
        (await Load(soon.Id)).LastDueSoonNotified.Should().Be(D(11, 4));
    }
}

/// <summary>提醒補寫失敗（來源丟例外）時只記 log，通知清單照常回傳。</summary>
public class NotificationReminderFailureTests : TaskTestBase
{
    private sealed class ThrowingSource : INotificationReminderSource
    {
        public Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct) => Task.FromResult(true);
        public Task AddDueAsync(string userId, DateOnly today, CancellationToken ct) => throw new InvalidOperationException("提醒來源壞了");
    }

    private sealed class CaptureLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
    }

    private readonly List<(LogLevel Level, string Message)> _logs = [];

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INotificationReminderSource, ThrowingSource>();
        services.AddSingleton<ILogger<GetNotificationsHandler>>(new CaptureLogger<GetNotificationsHandler>(_logs));
    }

    [Fact]
    public async Task SourceThrows_ListStillReturned_AndLogged()
    {
        Db.Context.Add(new Notification
        {
            UserId = "sales01", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "舊通知", Link = "/tasks?open=1",
            SourceType = "task", SourceId = 1, CreatedTime = new DateTime(2026, 11, 3, 9, 0, 0)
        });
        await Db.Context.SaveChangesAsync();
        As("sales01");

        var r = await Send(new GetNotificationsRequest());

        r.IsSuccess.Should().BeTrue(r.Message);
        ((NotificationListResponse)r.Data!).Items.Select(i => i.Title).Should().Equal("舊通知");
        _logs.Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("提醒"));
    }
}
