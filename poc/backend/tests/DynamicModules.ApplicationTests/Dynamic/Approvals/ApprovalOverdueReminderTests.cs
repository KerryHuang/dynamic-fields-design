using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>逾期提醒（設計 §12、§20.4 第 26 條；計畫 a3e、U3、U6、Review Focus 第 5 條）：讀通知清單時補寫，不跑背景排程。</summary>
public class ApprovalOverdueReminderTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

    private static DateTime T(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    /// <summary>流程：第 1 關 mgrA（dueHours 24，預設）→ 第 2 關 fin1；一張單 11-04 09:00 送出，第 1 關期限 11-05 09:00。</summary>
    private async Task<(PurchaseRig R, long Id)> Arrange(int? dueHours = 24, Func<Task>? beforeSubmit = null)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-g", r.Confirmed, 1, Def(null, Stage("s1", Users("mgrA"), dueHours: dueHours), Stage("s2", Users("fin1"), dueHours: 48)));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)]))
            .IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        if (beforeSubmit is not null) await beforeSubmit();
        return (r, await Submit(r));
    }

    private async Task<long> Submit(PurchaseRig r)
    {
        As("buyer1");
        var po = await NewPo(r, price: 20000);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    private async Task<NotificationListResponse> Read(string user)
    {
        As(user);
        var res = await Send(new GetNotificationsRequest(50));
        res.IsSuccess.Should().BeTrue(res.Message);
        Db.Context.ChangeTracker.Clear();
        return (NotificationListResponse)res.Data!;
    }

    private static int Overdue(NotificationListResponse list) => list.Items.Count(i => i.Kind == NotificationKinds.ApprovalOverdue);

    private ApprovalTask Open(long id, string user) =>
        TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task SetOverride(PurchaseRig r, string value)
    {
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalOverdueReminderHours.Code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Overdue_FirstRead_OneReminder_LastRemindedSet()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 10);

        var list = await Read("mgrA");

        Overdue(list).Should().Be(1);
        var n = list.Items.Single(i => i.Kind == NotificationKinds.ApprovalOverdue);
        n.Title.Should().Be(Inst(id).TitleSnapshot);
        n.Link.Should().Contain($"/{Inst(id).RecordId}");
        Open(id, "mgrA").LastRemindedTime.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task Overdue_SecondReadWithinInterval_NoDuplicate()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 10);
        await Read("mgrA");
        _clock.Now = T(6, 9, 59);   // 距離上次提醒 23 小時 59 分

        Overdue(await Read("mgrA")).Should().Be(1);
        Open(id, "mgrA").LastRemindedTime.Should().Be(T(5, 10));
    }

    [Fact]
    public async Task Overdue_AfterInterval_RemindsAgain()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 10);
        await Read("mgrA");
        _clock.Now = T(6, 10, 1);   // 24 小時又 1 分

        Overdue(await Read("mgrA")).Should().Be(2);
        Open(id, "mgrA").LastRemindedTime.Should().Be(T(6, 10, 1));
    }

    [Fact]
    public async Task Overdue_IntervalUsesModuleOverride()
    {
        var (r, _) = await Arrange();
        await SetOverride(r, "2");
        _clock.Now = T(5, 10);
        await Read("mgrA");
        _clock.Now = T(5, 11, 59);
        Overdue(await Read("mgrA")).Should().Be(1);
        _clock.Now = T(5, 12, 1);

        Overdue(await Read("mgrA")).Should().Be(2);
    }

    [Fact]
    public async Task NotYetDue_OrNoDueHours_NoReminder()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 8, 59);   // 期限 11-05 09:00 之前
        Overdue(await Read("mgrA")).Should().Be(0);
        Open(id, "mgrA").LastRemindedTime.Should().BeNull();

        var (_, none) = await ArrangeNoDue();
        _clock.Now = T(30, 9);
        Overdue(await Read("mgrA")).Should().Be(1);   // 只有第一張（有期限）逾期；沒設逾期時數的那張永不提醒
        Open(none, "mgrA").DueTime.Should().BeNull();
        Open(none, "mgrA").LastRemindedTime.Should().BeNull();
    }

    private async Task<(PurchaseRig R, long Id)> ArrangeNoDue()
    {
        _clock.Now = T(4, 9);
        As("admin");
        var r = await BuildPurchase("pn");
        await SeedFlow(r, "pn-g", r.Confirmed, 1, Def(null, Stage("s1", Users("mgrA"))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)]))
            .IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return (r, await Submit(r));
    }

    [Fact]
    public async Task Overdue_ClosedModuleOrEndedInstance_None()
    {
        var (r, id) = await Arrange();
        var other = await Submit(r);
        _clock.Now = T(5, 10);
        As("buyer1");
        (await Send(new WithdrawApprovalRequest(other, Inst(other).RowVersion, null))).IsSuccess.Should().BeTrue();   // 實例已結束
        await Db.Context.Set<Module>().Where(m => m.Id == r.ModuleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        Overdue(await Read("mgrA")).Should().Be(0);   // 模組關閉的那一張也不提醒
        TasksOf(id).Concat(TasksOf(other)).Should().OnlyContain(t => t.LastRemindedTime == null);

        await Db.Context.Set<Module>().Where(m => m.Id == r.ModuleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Active));
        Db.Context.ChangeTracker.Clear();

        Overdue(await Read("mgrA")).Should().Be(1);   // 重新開啟後恢復，已撤回的那張仍不提醒
    }

    [Fact]
    public async Task Overdue_TransferredTask_NewAssigneeOnly()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 10);
        Overdue(await Read("mgrA")).Should().Be(1);
        As("mgrA");
        (await Send(new TransferTaskRequest(Open(id, "mgrA").Id, Inst(id).RowVersion, "emp1", null))).IsSuccess.Should().BeTrue();

        Overdue(await Read("emp1")).Should().Be(1);   // 新任務沒有提醒紀錄，期限沿用（U3）已逾期，接手後第一次讀就收到
        _clock.Now = T(7, 10);
        Overdue(await Read("mgrA")).Should().Be(1);   // 舊負責人不再收
        Overdue(await Read("emp1")).Should().Be(2);
    }

    [Fact]
    public async Task Overdue_DelegatedTask_DelegateOnly()
    {
        var (_, id) = await Arrange(beforeSubmit: async () =>
        {
            As("mgrA");
            (await Send(new SaveMyApprovalDelegateRequest("emp1", new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 20)))).IsSuccess.Should().BeTrue();
        });
        Open(id, "emp1").OriginalUserId.Should().Be("mgrA");
        _clock.Now = T(5, 10);

        Overdue(await Read("mgrA")).Should().Be(0);
        Overdue(await Read("emp1")).Should().Be(1);
    }

    [Fact]
    public async Task AnyDue_FalseWhenNothingDue_NoUpkeepWrite()
    {
        var (_, id) = await Arrange();
        var before = Db.Context.Set<Notification>().AsNoTracking().Count();
        _clock.Now = T(4, 20);

        var list = await Read("mgrA");

        Overdue(list).Should().Be(0);
        Db.Context.Set<Notification>().AsNoTracking().Count().Should().Be(before);
        Open(id, "mgrA").LastRemindedTime.Should().BeNull();
    }

    [Fact]
    public async Task Overdue_UsesBusinessNow()
    {
        var (_, id) = await Arrange(dueHours: 14);   // 11-04 09:00 送出，期限 23:00
        _clock.Now = T(4, 22, 59);
        Overdue(await Read("mgrA")).Should().Be(0);
        _clock.Now = T(4, 23, 30);   // 營運日深夜

        Overdue(await Read("mgrA")).Should().Be(1);
        Open(id, "mgrA").LastRemindedTime.Should().Be(T(4, 23, 30));
    }

    private int NotificationCount() => Db.Context.Set<Notification>().AsNoTracking().Count(n => n.Kind == NotificationKinds.ApprovalOverdue);

    private async Task SetStatus(long moduleId, ModuleStatus status)
    {
        await Db.Context.Set<Module>().Where(m => m.Id == moduleId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status));   // 不清追蹤：模擬另一個請求的提交，這個請求追蹤中的實體不會被刷新
    }

    [Fact]
    public async Task Overdue_ModuleClosedBetweenPrecheckAndUpkeep_NoReminder()
    {
        var (r, id) = await Arrange();
        _clock.Now = T(5, 10);
        As("mgrA");
        using var scope = NewScope();   // 同一個請求（同一個 DbContext）：預查在前、upkeep 在後，中間模組被關閉
        var source = scope.ServiceProvider.GetServices<INotificationReminderSource>().OfType<ApprovalReminderSource>().Single();
        (await source.AnyDueAsync("mgrA", _clock.Today, CancellationToken.None)).Should().BeTrue();
        await SetStatus(r.ModuleId, ModuleStatus.Closed);   // 另一個請求提交的關閉

        await source.AddDueAsync("mgrA", _clock.Today, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);

        NotificationCount().Should().Be(0);
        Open(id, "mgrA").LastRemindedTime.Should().BeNull();
    }

    [Fact]
    public async Task Overdue_TaskAlreadyClaimedByAnotherRequest_NoReminder()
    {
        var (_, id) = await Arrange();
        _clock.Now = T(5, 10);
        As("mgrA");
        using var scope = NewScope();
        var source = scope.ServiceProvider.GetServices<INotificationReminderSource>().OfType<ApprovalReminderSource>().Single();
        (await source.AnyDueAsync("mgrA", _clock.Today, CancellationToken.None)).Should().BeTrue();
        var taskId = Open(id, "mgrA").Id;
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == taskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastRemindedTime, _clock.Now));   // 另一個請求先認領

        await source.AddDueAsync("mgrA", _clock.Today, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);

        NotificationCount().Should().Be(0);
        Open(id, "mgrA").LastRemindedTime.Should().Be(_clock.Now);
    }
}
