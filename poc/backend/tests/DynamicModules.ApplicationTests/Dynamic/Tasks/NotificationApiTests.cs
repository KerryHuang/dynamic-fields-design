using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>通知清單：讀取時補到期／逾期提醒（一天最多一則）、清掉 90 天前的、標為已讀（設計 §6.2、§6.3）。</summary>
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
    public async Task LeadDays_ModuleOverride_RemindsThatManyDaysBefore()
    {
        var t = await BuildTarget();
        As("admin");
        (await Send(new UpdateModulePolicyRequest(t.Key, "task.reminderLeadDays", J("3")))).IsSuccess.Should().BeTrue();
        await SeedTask(title: "三天後", assignee: "sales01", due: D(11, 7), moduleId: t.ModuleId);
        await SeedTask(title: "明天（該模組）", assignee: "sales01", due: D(11, 5), moduleId: t.ModuleId);
        await SeedTask(title: "明天（無模組）", assignee: "sales01", due: D(11, 5));

        var list = await Get("sales01");

        list.Items.Where(i => i.Kind == NotificationKinds.TaskDueSoon).Select(i => i.Title)
            .Should().BeEquivalentTo(["三天後", "明天（無模組）"]);
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

        var filter = new DynamicModules.Domain.Repositories.Dynamic.ReminderFilter([D(11, 7)], [], OverdueWithoutModule: true);
        var due = await repo.ListReminderDueAsync("sales01", D(11, 4), filter, track: false, CancellationToken.None);
        due.Select(x => x.Title).Should().Equal("對的日期", "無模組逾期（開）");

        var off = new DynamicModules.Domain.Repositories.Dynamic.ReminderFilter([], [], OverdueWithoutModule: false);
        (await repo.AnyReminderDueAsync("sales01", D(11, 4), off, CancellationToken.None)).Should().BeFalse();
        (await repo.ListReminderModuleIdsAsync("sales01", D(11, 4), 30, CancellationToken.None))
            .Should().BeEquivalentTo(new long?[] { null, t.ModuleId });
    }

    [Fact]
    public async Task Overdue_NextDay_RemindsAgain()
    {
        await SeedTask(title: "已逾期", assignee: "sales01", due: D(11, 3));
        await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await Get("sales01");

        _clock.Now = new DateTime(2026, 11, 5, 8, 0, 0);
        var list = await Get("sales01");

        Count(list, NotificationKinds.TaskOverdue).Should().Be(2, "逾期的任務隔天再提醒一次");
        Count(list, NotificationKinds.TaskDueSoon).Should().Be(1, "今天到期的不算明天到期，也還沒逾期");
    }

    [Fact]
    public async Task Reminder_UsesBusinessDate()
    {
        _clock.Now = new DateTime(2026, 11, 4, 23, 30, 0);   // 台北 11-04 深夜（UTC 已是 15:30，仍是同一營業日）
        await SeedTask(title: "明天交", assignee: "sales01", due: D(11, 5));
        await SeedTask(title: "今天交", assignee: "sales01", due: D(11, 4));

        var list = await Get("sales01");

        list.Items.Select(i => (i.Kind, i.Title)).Should().Equal((NotificationKinds.TaskDueSoon, "明天交"));
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
