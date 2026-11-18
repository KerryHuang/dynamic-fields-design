using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>通知清單：讀取時補到期提醒（即將到期每任務一次、逾期一天最多一則且不疊加未讀）、清掉 90 天前的、標為已讀（設計 §6.2、§6.3）。</summary>
public class NotificationApiTests : TaskTestBase
{
    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private async Task<NotificationListResponse> Get(string userId, int take = 20)
    {
        As(userId);
        var r = await Send(new GetNotificationsRequest(take));
        r.IsSuccess.Should().BeTrue(r.Message);
        Db.Context.ChangeTracker.Clear();
        return (NotificationListResponse)r.Data!;
    }

    private async Task<long> SeedNotification(string userId, DateTime created, string kind = NotificationKinds.TaskAssigned, long? sourceId = 1)
    {
        var n = new Notification
        {
            UserId = userId, Kind = kind, TitleSnapshot = "準備資料", Link = $"/tasks?open={sourceId}", SourceType = "task",
            SourceId = sourceId, CreatedTime = created
        };
        Db.Context.Add(n);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return n.Id;
    }

    private static int Count(NotificationListResponse list, string kind) => list.Items.Count(i => i.Kind == kind);

    [Fact]
    public async Task Reminders_AtMostOncePerDay()
    {
        var soon = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3), status: TaskItemStatus.InProgress);
        await SeedTask(title: "已完成", assignee: "sales01", due: D(11, 3), status: TaskItemStatus.Done);
        await SeedTask(title: "後天交", assignee: "sales01", due: D(11, 6));
        await SeedTask(title: "別人的", assignee: "sales02", due: D(11, 5));

        await Get("sales01");
        var list = await Get("sales01");

        Count(list, NotificationKinds.TaskDueSoon).Should().Be(1);
        Count(list, NotificationKinds.TaskOverdue).Should().Be(1);
        list.Items.Should().HaveCount(2);
        list.UnreadCount.Should().Be(2);
        var dueSoon = list.Items.Single(i => i.Kind == NotificationKinds.TaskDueSoon);
        dueSoon.Title.Should().Be("明天交");
        dueSoon.Link.Should().Be($"/tasks?open={soon.Id}");
        dueSoon.Read.Should().BeFalse();
        (await Load(soon.Id)).LastDueSoonNotified.Should().Be(D(11, 4));
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == "sales02")).Should().Be(0, "只補讀取者自己的提醒");
    }

    [Fact]
    public async Task LeadDays_ModuleOverride_RemindsAnyDayWithinWindow()
    {
        var t = await BuildTarget();
        As("admin");
        (await Send(new UpdateModulePolicyRequest(t.Key, "task.reminderLeadDays", J("3")))).IsSuccess.Should().BeTrue();
        await SeedTask(title: "三天後", assignee: "sales01", due: D(11, 7), moduleId: t.ModuleId);
        await SeedTask(title: "明天（該模組）", assignee: "sales01", due: D(11, 5), moduleId: t.ModuleId);
        await SeedTask(title: "明天（無模組）", assignee: "sales01", due: D(11, 5));

        var list = await Get("sales01");

        list.Items.Where(i => i.Kind == NotificationKinds.TaskDueSoon).Select(i => i.Title)
            .Should().BeEquivalentTo(["三天後", "明天（該模組）", "明天（無模組）"], "到期前 N 天內的任何一天都補（使用者 2026-11-12 裁決）");
    }

    [Fact]
    public async Task RemindOverdue_Off_NoOverdueReminder()
    {
        await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        As("admin");
        (await Send(new UpdateSystemPolicyRequest("task.remindOverdue", J("false")))).IsSuccess.Should().BeTrue();

        var source = Svc<IEnumerable<INotificationReminderSource>>().OfType<TaskReminderSource>().Single();
        (await source.AnyDueAsync("sales01", D(11, 4), CancellationToken.None)).Should().BeFalse("只有逾期任務、又關了逾期提醒，不該觸發 upkeep");
        var list = await Get("sales01");

        Count(list, NotificationKinds.TaskOverdue).Should().Be(0);
    }

    [Fact]
    public async Task ReminderFilter_ExcludesInDatabase_WrongDateAndOverdueOffModule()
    {
        var t = await BuildTarget();
        await SeedTask(title: "對的日期", assignee: "sales01", due: D(11, 7));
        await SeedTask(title: "錯的日期", assignee: "sales01", due: D(11, 5));
        await SeedTask(title: "模組逾期（關）", assignee: "sales01", due: D(11, 3), moduleId: t.ModuleId);
        await SeedTask(title: "無模組逾期（開）", assignee: "sales01", due: D(11, 2));
        var repo = Svc<DynamicModules.Domain.Repositories.Dynamic.ITaskRepository>();

        var filter = new DynamicModules.Domain.Repositories.Dynamic.ReminderFilter(D(11, 5), [], OverdueWithoutModule: true);
        var due = await repo.ListReminderDueAsync("sales01", D(11, 4), filter, CancellationToken.None);
        due.Select(x => x.Title).Should().Equal("錯的日期", "無模組逾期（開）");   // 「對的日期」11/7 超過 DueSoonUntil

        var off = new DynamicModules.Domain.Repositories.Dynamic.ReminderFilter(null, [], OverdueWithoutModule: false);
        (await repo.AnyReminderDueAsync("sales01", D(11, 4), off, CancellationToken.None)).Should().BeFalse();
        (await repo.ListReminderModuleIdsAsync("sales01", D(11, 4), 30, CancellationToken.None))
            .Should().BeEquivalentTo(new long?[] { null, t.ModuleId });
    }

    [Fact]
    public async Task Overdue_NextDay_UnreadExists_NotAddedAgain()
    {
        var late = await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await Get("sales01");

        _clock.Now = new DateTime(2026, 11, 5, 8, 0, 0);
        var list = await Get("sales01");

        Count(list, NotificationKinds.TaskOverdue).Should().Be(1, "上一則逾期通知還沒讀，不疊加（O1 計畫裁決 B1）");
        (await Load(late.Id)).LastOverdueNotified.Should().Be(D(11, 5), "今天的提醒由那則未讀的代表，戳記照蓋");
        Count(list, NotificationKinds.TaskDueSoon).Should().Be(1, "11/5 到期的任務在 11/4 已補過，同一任務只補一次");
    }

    [Fact]
    public async Task Overdue_UnreadSuppresses_ReadThenNextDayRemindsAgain()
    {
        await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(1);

        _clock.Now = new DateTime(2026, 11, 5, 9, 0, 0);
        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(1, "沒讀不疊加");
        As("sales01");
        (await Send(new MarkAllNotificationsReadRequest())).IsSuccess.Should().BeTrue();
        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(1, "今天的戳記已蓋，同一天讀完不再補");

        _clock.Now = new DateTime(2026, 11, 6, 9, 0, 0);
        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(2, "讀過之後隔天再補");
    }

    [Fact]
    public async Task Overdue_ReadSameDay_NotAgainSameDay()
    {
        await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(1);
        As("sales01");
        (await Send(new MarkAllNotificationsReadRequest())).IsSuccess.Should().BeTrue();

        Count(await Get("sales01"), NotificationKinds.TaskOverdue).Should().Be(1, "一天最多一則");
    }

    [Fact]
    public async Task DueSoon_OnlyOncePerTask_AcrossDays()
    {
        var t = await BuildTarget();
        As("admin");
        (await Send(new UpdateModulePolicyRequest(t.Key, "task.reminderLeadDays", J("3")))).IsSuccess.Should().BeTrue();
        await SeedTask(title: "三天後", assignee: "sales01", due: D(11, 7), moduleId: t.ModuleId);
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);

        _clock.Now = new DateTime(2026, 11, 5, 9, 0, 0);
        await Get("sales01");
        _clock.Now = new DateTime(2026, 11, 6, 9, 0, 0);
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1, "同一任務只補一次");
    }

    [Fact]
    public async Task DueSoon_TaskCreatedInsideWindow_RemindedOnce()
    {
        await Get("sales01");
        await SeedTask(title: "臨時交辦", assignee: "sales01", due: D(11, 5), created: new DateTime(2026, 11, 4, 10, 0, 0));

        await Get("sales01");
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1, "已在範圍內才建立的任務也補一次");
    }

    [Fact]
    public async Task DueSoon_LegacyStampFromOldRule_NotRemindedAgain()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await Db.Context.Set<TaskItem>().Where(x => x.Id == task.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.LastDueSoonNotified, D(11, 3)));

        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(0, "上線前已被舊規則提醒過的任務，上線後不再補");
    }

    [Fact]
    public async Task TryMarkDueSoon_SecondCall_False()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        var repo = Svc<DynamicModules.Domain.Repositories.Dynamic.ITaskRepository>();

        (await repo.TryMarkDueSoonNotifiedAsync(task.Id, D(11, 4), CancellationToken.None)).Should().BeTrue();
        (await repo.TryMarkDueSoonNotifiedAsync(task.Id, D(11, 5), CancellationToken.None)).Should().BeFalse();
        (await Load(task.Id)).LastDueSoonNotified.Should().Be(D(11, 4));
    }

    [Fact]
    public async Task TryMarkOverdue_SameDayTwice_SecondFalse_NextDayTrue()
    {
        var task = await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        var repo = Svc<DynamicModules.Domain.Repositories.Dynamic.ITaskRepository>();

        (await repo.TryMarkOverdueNotifiedAsync(task.Id, D(11, 4), CancellationToken.None)).Should().BeTrue();
        (await repo.TryMarkOverdueNotifiedAsync(task.Id, D(11, 4), CancellationToken.None)).Should().BeFalse();
        (await repo.TryMarkOverdueNotifiedAsync(task.Id, D(11, 5), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task DueSoon_ResetAfterDueDateChanged()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);

        As("admin");
        (await Send(new UpdateTaskRequest(task.Id, J("""{"dueDate":"2026-11-20"}""")))).IsSuccess.Should().BeTrue();
        (await Load(task.Id)).LastDueSoonNotified.Should().BeNull("改到期日後重新計（O1 計畫裁決 D2）");

        _clock.Now = new DateTime(2026, 11, 19, 9, 0, 0);
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(2);
    }

    [Fact]
    public async Task Reminders_ResetAfterAssigneeChanged()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);

        As("admin");
        (await Send(new UpdateTaskRequest(task.Id, J("""{"assigneeUserId":"sales02"}""")))).IsSuccess.Should().BeTrue();
        var moved = await Load(task.Id);
        moved.LastDueSoonNotified.Should().BeNull("換負責人後重新計（D2）");
        moved.LastOverdueNotified.Should().BeNull();

        Count(await Get("sales02"), NotificationKinds.TaskDueSoon).Should().Be(1, "新負責人也會收到");
    }

    [Fact]
    public async Task ResetReminderStamps_NullToNull_StillWritesNull()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 10));
        var repo = Svc<DynamicModules.Domain.Repositories.Dynamic.ITaskRepository>();
        var tracked = await Db.Context.Set<TaskItem>().SingleAsync(t => t.Id == task.Id);
        // 載入之後、存檔之前，另一個請求蓋了戳記（ExecuteUpdate 不更新已追蹤的實體，這裡的 tracked 仍是 null）
        await Db.Context.Set<TaskItem>().Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.LastDueSoonNotified, D(11, 4)));

        tracked.DueDate = D(11, 20);
        repo.ResetReminderStamps(tracked, overdueToo: false);
        await Db.Context.SaveChangesAsync();

        (await Load(task.Id)).LastDueSoonNotified.Should().BeNull("null 設回 null 也要寫進 UPDATE");
    }

    [Fact]
    public async Task DueSoon_ReopenedSameDueDate_NotRemindedAgain()
    {
        var task = await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);

        As("admin");
        (await Send(new UpdateTaskRequest(task.Id, J("""{"status":"done"}""")))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateTaskRequest(task.Id, J("""{"status":"todo"}""")))).IsSuccess.Should().BeTrue();
        (await Load(task.Id)).LastDueSoonNotified.Should().Be(D(11, 4), "完成後重開、到期日沒改，不重新計（O1 計畫裁決 B3）");

        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);
        _clock.Now = new DateTime(2026, 11, 5, 9, 0, 0);
        Count(await Get("sales01"), NotificationKinds.TaskDueSoon).Should().Be(1);
    }

    [Fact]
    public async Task Reminder_UsesBusinessDate()
    {
        _clock.Now = new DateTime(2026, 11, 4, 23, 30, 0);   // 台北 11-04 深夜（UTC 已是 15:30，仍是同一營業日）
        await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await SeedTask(title: "今天交", assignee: "sales01", due: D(11, 4));

        var list = await Get("sales01");

        list.Items.Select(i => (i.Kind, i.Title)).Should().BeEquivalentTo(
            [(NotificationKinds.TaskDueSoon, "明天交"), (NotificationKinds.TaskDueSoon, "今天交")], "範圍含到期當天（O1 計畫裁決 D1）");
    }

    [Fact]
    public async Task Purge_Older90Days()
    {
        await SeedNotification("sales01", new DateTime(2026, 8, 5, 12, 0, 0));
        var kept = await SeedNotification("sales01", new DateTime(2026, 8, 7, 12, 0, 0));
        await SeedNotification("sales02", new DateTime(2026, 8, 1, 12, 0, 0));

        var list = await Get("sales01");

        list.Items.Select(i => i.Id).Should().Equal(kept);
        (await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == "sales01").Select(n => n.Id).ToListAsync())
            .Should().Equal(kept);
        (await Db.Context.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == "sales02")).Should().Be(1, "只清讀取者自己的");
    }

    [Fact]
    public async Task Purge_FollowsRetentionPolicy()
    {
        As("admin");
        (await Send(new UpdateSystemPolicyRequest("notification.retentionDays", J("30")))).IsSuccess.Should().BeTrue();
        await SeedNotification("sales01", new DateTime(2026, 10, 4, 8, 59, 0));    // 31 天前
        var boundary = await SeedNotification("sales01", new DateTime(2026, 10, 5, 9, 0, 0));   // 剛好 30 天前：不早於 cutoff，保留
        var kept = await SeedNotification("sales01", new DateTime(2026, 10, 6, 9, 0, 0));   // 29 天前

        var list = await Get("sales01");

        list.Items.Select(i => i.Id).Should().BeEquivalentTo([boundary, kept]);
    }

    [Fact]
    public async Task List_LatestFirst_TakeLimits_SourceMissingStillShown()
    {
        var older = await SeedNotification("sales01", new DateTime(2026, 11, 1, 9, 0, 0), sourceId: 999);   // 任務 999 不存在
        var newer = await SeedNotification("sales01", new DateTime(2026, 11, 3, 9, 0, 0), NotificationKinds.TaskCommented, 998);

        var all = await Get("sales01");
        var one = await Get("sales01", take: 1);

        all.Items.Select(i => i.Id).Should().Equal(newer, older);
        all.Items[1].Title.Should().Be("準備資料");
        one.Items.Select(i => i.Id).Should().Equal(newer);
        one.UnreadCount.Should().Be(2, "未讀數不受 take 影響");
    }

    [Fact]
    public async Task MarkRead_OthersNotification_404()
    {
        var mine = await SeedNotification("sales01", new DateTime(2026, 11, 3, 9, 0, 0));
        var others = await SeedNotification("sales02", new DateTime(2026, 11, 3, 9, 0, 0));

        As("sales01");
        var r = await Send(new MarkNotificationReadRequest(others));
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("找不到通知");
        (await Send(new MarkNotificationReadRequest(mine))).IsSuccess.Should().BeTrue();

        var list = await Get("sales01");
        list.Items.Single().Read.Should().BeTrue();
        list.UnreadCount.Should().Be(0);
        (await Get("sales02")).UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadAll_UnreadZero()
    {
        await SeedNotification("sales01", new DateTime(2026, 11, 2, 9, 0, 0));
        await SeedNotification("sales01", new DateTime(2026, 11, 3, 9, 0, 0));
        await SeedNotification("sales02", new DateTime(2026, 11, 3, 9, 0, 0));

        As("sales01");
        (await Send(new MarkAllNotificationsReadRequest())).IsSuccess.Should().BeTrue();

        var list = await Get("sales01");
        list.UnreadCount.Should().Be(0);
        list.Items.Should().OnlyContain(i => i.Read);
        (await Get("sales02")).UnreadCount.Should().Be(1);
    }
}

/// <summary>讀取時補提醒只透過 <see cref="INotificationReminderSource"/>：另一種來源（例如之後的簽核）註冊進來就會被呼叫。</summary>
public class NotificationReminderSourceTests : TaskTestBase
{
    private sealed class ProbeSource : INotificationReminderSource
    {
        public int Asked { get; private set; }
        public int Added { get; private set; }
        public bool Due { get; set; }

        public Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(Due);
        }

        public Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
        {
            Added++;
            return Task.CompletedTask;
        }
    }

    private readonly ProbeSource _probe = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<INotificationReminderSource>(_probe);

    [Fact]
    public async Task Upkeep_OnlyWhenSomeSourceIsDue_CallsEverySource()
    {
        (await Send(new GetNotificationsRequest())).IsSuccess.Should().BeTrue();
        _probe.Asked.Should().Be(1);
        _probe.Added.Should().Be(0, "沒有到期的就不送寫入請求");

        _probe.Due = true;
        (await Send(new GetNotificationsRequest())).IsSuccess.Should().BeTrue();
        _probe.Added.Should().Be(1);
    }
}
