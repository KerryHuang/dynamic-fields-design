using System.Text.Json;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 背景排程的通知維護（維運設計 §10.2.9）：不打開鈴鐺也收到提醒、同一天不重複、與讀通知的惰性路徑先後或並行都不重複、
/// 清舊通知、跳過停用帳號、一人失敗其他人照常、用營業日、逾期不疊加未讀。
/// </summary>
public sealed class NotificationUpkeepJobTests : IDisposable
{
    private readonly FileDatabaseApp _app = new();
    private readonly ManualClock _clock = new(Start);
    private ThrowingForUser? _throwing;

    public NotificationUpkeepJobTests()
    {
        _app.Clock.Fixed = new DateTime(2026, 11, 4, 9, 0, 0);
    }

    public void Dispose() => _app.Dispose();

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private JobRunner NewRunner() => Runner(_app, _clock, _app.Services.GetServices<IScheduledJob>());

    private async Task<JobRunner> Ready()
    {
        var runner = NewRunner();
        await runner.EnsureJobsAsync(CancellationToken.None);
        return runner;
    }

    /// <summary>跑到下一次排程（間隔出廠 60 分鐘）。</summary>
    private async Task NextRun(JobRunner runner)
    {
        _clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);
    }

    private async Task<long> SeedTask(string title, string assignee, DateOnly due)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var task = new TaskItem
        {
            Title = title, CreatedBy = "admin", AssigneeUserId = assignee, DueDate = due, Status = TaskItemStatus.Todo,
            CreatedTime = new DateTime(2026, 11, 1, 9, 0, 0)
        };
        task.Participants.Add(new TaskParticipant { UserId = "admin", Reason = TaskParticipantReason.Creator });
        task.Participants.Add(new TaskParticipant { UserId = assignee, Reason = TaskParticipantReason.Assignee });
        db.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<List<Notification>> Notifications(string userId, string? kind = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Notification>().AsNoTracking()
            .Where(n => n.UserId == userId && (kind == null || n.Kind == kind)).ToListAsync();
    }

    private async Task MarkAllRead(string userId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Notification>().Where(n => n.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadTime, new DateTime(2026, 11, 4, 12, 0, 0)));
    }

    [Fact]
    public async Task Job_RemindsWithoutUserOpeningBell()
    {
        await SeedTask("明天交", "sales01", D(11, 5));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales01", NotificationKinds.TaskDueSoon)).Should().ContainSingle().Which.TitleSnapshot.Should().Be("明天交");
        var info = JsonDocument.Parse(await (await _app.AsUser().GetAsync("/api/v3/dynamic/system/ops/info")).Content.ReadAsStringAsync()).RootElement;
        var job = info.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("jobKey").GetString() == "notification.upkeep");
        job.GetProperty("lastStatus").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task Job_SameDayTwice_NoDuplicate()
    {
        await SeedTask("明天交", "sales01", D(11, 5));
        await SeedTask("已逾期", "sales01", D(11, 3));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);
        await NextRun(runner);

        (await Notifications("sales01", NotificationKinds.TaskDueSoon)).Should().HaveCount(1);
        (await Notifications("sales01", NotificationKinds.TaskOverdue)).Should().HaveCount(1);
        (await Row(_app, "notification.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
    }

    [Fact]
    public async Task Job_ThenLazyPath_NoDuplicate()
    {
        await SeedTask("明天交", "sales01", D(11, 5));
        await SeedTask("已逾期", "sales01", D(11, 3));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);
        (await _app.AsUser("sales01").GetAsync("/api/v3/dynamic/notifications")).EnsureSuccessStatusCode();

        (await Notifications("sales01")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Job_AndLazyPath_Concurrent_NoDuplicate()
    {
        await SeedTask("明天交", "sales01", D(11, 5));
        await SeedTask("已逾期", "sales01", D(11, 3));
        var runner = await Ready();
        var client = _app.AsUser("sales01");

        await Within(Task.WhenAll(
            runner.TickAsync(CancellationToken.None),
            client.GetAsync("/api/v3/dynamic/notifications"),
            client.GetAsync("/api/v3/dynamic/notifications")));

        (await Notifications("sales01", NotificationKinds.TaskDueSoon)).Should().HaveCount(1);
        (await Notifications("sales01", NotificationKinds.TaskOverdue)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Job_PurgesOlderThanRetention()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Add(new Notification
            {
                UserId = "sales01", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "舊的", Link = "/tasks?open=1", SourceType = "task",
                SourceId = 1, CreatedTime = new DateTime(2026, 8, 5, 9, 0, 0)   // 91 天前
            });
            await db.SaveChangesAsync();
        }
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales01")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpkeepJob_SkipsInactiveUsers()
    {
        await SeedTask("明天交", "sales02", D(11, 5));
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().Where(u => u.UserId == "sales02")
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales02")).Should().BeEmpty();
    }

    /// <summary>整批開始後才被停用（審查 P2）：輪到他時交易內重新確認，不補提醒、不蓋戳記、不清通知。</summary>
    [Fact]
    public async Task UpkeepJob_UserDeactivatedMidBatch_Skipped()
    {
        _app.ExtraServices = s => s.AddSingleton<INotificationReminderSource>(new HookForUser("sales01", async () =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().Where(u => u.UserId == "sales02")
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        }));
        var taskId = await SeedTask("明天交", "sales02", D(11, 5));
        await SeedOld("sales02");
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales02")).Should().ContainSingle(n => n.TitleSnapshot == "舊的", "沒補提醒、也沒清舊通知");
        await using var check = _app.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<TaskItem>().AsNoTracking().SingleAsync(t => t.Id == taskId))
            .LastDueSoonNotified.Should().BeNull("沒有蓋戳記，重新啟用後還會補");
        (await Row(_app, "notification.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
    }

    /// <summary>做到一半（判斷完 sales01 之後）租約被別的實例拿走（審查 P1）：之後每位使用者的交易都先確認租約，失效就不寫，這次執行不寫結果。</summary>
    [Fact]
    public async Task UpkeepJob_LeaseTakenMidBatch_StopsWriting()
    {
        _app.ExtraServices = s => s.AddSingleton<INotificationReminderSource>(new HookForUser("sales01", () =>
            Update(_app, "notification.upkeep", j => { j.LeaseOwner = "other"; j.LeaseUntilUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(5); })));
        var taskId = await SeedTask("明天交", "sales02", D(11, 5));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales02")).Should().BeEmpty("租約失效後的交易整筆回滾");
        await using (var check = _app.Services.CreateAsyncScope())
            (await check.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<TaskItem>().AsNoTracking().SingleAsync(t => t.Id == taskId))
                .LastDueSoonNotified.Should().BeNull();
        var row = await Row(_app, "notification.upkeep");
        row.LeaseOwner.Should().Be("other");
        row.LastStatus.Should().BeNull();
        JobLogs(_app).Should().Contain(r => r.EventId.Id == JobRunner.JobFinished.Id && (string?)r.Values["Result"] == "leaseLost");
    }

    private async Task SeedOld(string userId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Add(new Notification
        {
            UserId = userId, Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "舊的", Link = "/tasks?open=1", SourceType = "task",
            SourceId = 1, CreatedTime = new DateTime(2026, 8, 5, 9, 0, 0)   // 91 天前
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpkeepJob_OneUserFails_OthersContinue_PartiallyFailed()
    {
        _throwing = new ThrowingForUser("sales01");
        _app.ExtraServices = s => s.AddSingleton<INotificationReminderSource>(_throwing);
        await SeedTask("明天交", "sales02", D(11, 5));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales02", NotificationKinds.TaskDueSoon)).Should().ContainSingle();
        var row = await Row(_app, "notification.upkeep");
        row.LastStatus.Should().Be(ScheduledJobStatus.PartiallyFailed);
        row.ConsecutiveFailures.Should().Be(1);
        row.LastError.Should().StartWith("1/");
        _app.Logs.Records.Should().Contain(r => r.Category == typeof(NotificationUpkeepJob).FullName && (string?)r.Values["UserId"] == "sales01");
    }

    [Fact]
    public async Task UpkeepJob_UsesBusinessDateNotUtcDate()
    {
        // 台北 11-05 07:00 是 UTC 11-04 23:00：營業日與 UTC 日期不同
        _app.Clock.Fixed = new DateTime(2026, 11, 5, 7, 0, 0);
        _clock.Advance(new DateTimeOffset(2026, 11, 4, 23, 0, 0, TimeSpan.Zero) - _clock.GetUtcNow());
        await SeedTask("後天交", "sales01", D(11, 6));
        await SeedTask("昨天到期", "sales01", D(11, 4));
        var runner = await Ready();

        await runner.TickAsync(CancellationToken.None);

        (await Notifications("sales01")).Select(n => (n.Kind, n.TitleSnapshot)).Should().BeEquivalentTo(
            [(NotificationKinds.TaskDueSoon, "後天交"), (NotificationKinds.TaskOverdue, "昨天到期")]);
    }

    [Fact]
    public async Task Job_OverdueUnread_NotStackedAcrossDays()
    {
        await SeedTask("已逾期", "sales01", D(11, 3));
        var runner = await Ready();

        foreach (var day in new[] { 4, 5, 6 })
        {
            _app.Clock.Fixed = new DateTime(2026, 11, day, 9, 0, 0);
            await NextRun(runner);
        }
        (await Notifications("sales01", NotificationKinds.TaskOverdue)).Should().HaveCount(1, "沒讀就不疊加");

        await MarkAllRead("sales01");
        _app.Clock.Fixed = new DateTime(2026, 11, 7, 9, 0, 0);
        await NextRun(runner);
        (await Notifications("sales01", NotificationKinds.TaskOverdue)).Should().HaveCount(2);
    }

    /// <summary>對指定使用者一律說要補、補的時候丟例外；其他人不參與。</summary>
    private sealed class ThrowingForUser(string userId) : INotificationReminderSource
    {
        public Task<bool> AnyDueAsync(string user, DateOnly today, CancellationToken ct) => Task.FromResult(user == userId);

        public Task AddDueAsync(string user, DateOnly today, CancellationToken ct) =>
            ActingScope.UserId == userId ? throw new InvalidOperationException("提醒來源壞了") : Task.CompletedTask;
    }

    /// <summary>輪到指定使用者做判斷時（交易外）先跑一段交錯動作，只跑一次；自己從不需要補。</summary>
    private sealed class HookForUser(string userId, Func<Task> hook) : INotificationReminderSource
    {
        private int _done;

        public async Task<bool> AnyDueAsync(string user, DateOnly today, CancellationToken ct)
        {
            if (user == userId && Interlocked.Exchange(ref _done, 1) == 0) await hook();
            return false;
        }

        public Task AddDueAsync(string user, DateOnly today, CancellationToken ct) => Task.CompletedTask;
    }
}
