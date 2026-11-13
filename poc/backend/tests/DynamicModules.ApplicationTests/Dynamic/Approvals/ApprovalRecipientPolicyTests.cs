using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>A10 備援對象、A11 異常通知對象改讀政策（收件人型別，出廠值 admins）。</summary>
public class ApprovalRecipientPolicyTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

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
        services.AddSingleton<ILogger<WriteApprovalAlertHandler>>(new CaptureLogger<WriteApprovalAlertHandler>(_logs));
        services.AddSingleton<ILogger<RecipientResolver>>(new CaptureLogger<RecipientResolver>(_logs));
    }

    private async Task SetOverride(PurchaseRig r, string value)
    {
        Db.Context.Add(new ModulePolicy
        {
            ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalDefaultFallbackApprovers.Code, Value = value, ModifiedUserId = "admin"
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task SetSystemAlert(string value)
    {
        Db.Context.Add(new SystemSetting { Key = PolicyCatalog.ApprovalAlertRecipients.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task Disable(string userId)
    {
        (await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == userId)).IsActive = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>流程 po-x 的關卡來源是發起人的主管；發起人 gm 沒有主管，所以每一關都找不到人而走備援。</summary>
    private async Task<PurchaseRig> RigWithManagerFlow(ApprovalDefinition? def = null, int stages = 1)
    {
        var r = await Scenario3();
        var list = Enumerable.Range(1, stages).Select(i => Stage($"m{i}", Manager(1))).ToArray();
        await SeedFlow(r, "po-x", r.Confirmed, 0, def ?? Def(null, list));
        return r;
    }

    private async Task<ServiceResult> SubmitAs(PurchaseRig r, string user, decimal price = 100)
    {
        As(user);
        var po = await NewPo(r, price: price);
        return await Send(new SubmitApprovalRequest(po.Id, r.Confirmed, Rec(po.Id).Row.RowVersion, null, null));
    }

    private static long InstanceOf(ServiceResult res) => ((SubmitApprovalResponse)res.Data!).InstanceId!.Value;

    private IEnumerable<string> OpenAssignees(long id) =>
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Order();

    private Task<ServiceResult> Approve(long id, string user)
    {
        As(user);
        var task = TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);
        return Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));
    }

    [Fact]
    public async Task Fallback_Factory_AllAdmins()
    {
        var r = await RigWithManagerFlow();
        var res = await SubmitAs(r, "gm");
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("admin");
        TasksOf(InstanceOf(res)).Should().OnlyContain(t => t.Reason == ApprovalTaskReason.Fallback);
    }

    [Fact]
    public async Task Fallback_ModuleOverrideGroup_UsesGroupMembers()
    {
        var r = await RigWithManagerFlow();
        await SetOverride(r, $"group:{FinanceGroup}");
        var res = await SubmitAs(r, "gm");
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("fin1", "fin2");
    }

    [Fact]
    public async Task Fallback_FlowDefinitionFallback_TakesPrecedence()
    {
        var r = await RigWithManagerFlow(Def(null, Stage("m1", Manager(1))) with { FallbackApprovers = Users("buyer2") });
        await SetOverride(r, $"group:{FinanceGroup}");
        var res = await SubmitAs(r, "gm");
        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenAssignees(InstanceOf(res)).Should().Equal("buyer2");
    }

    [Fact]
    public async Task Fallback_OnlyDisabledUser_Rejects409()
    {
        var r = await RigWithManagerFlow();
        await SetOverride(r, "user:fin1");
        await Disable("fin1");
        var res = await SubmitAs(r, "gm");
        res.IsConflict.Should().BeTrue(Dump(res));
        Db.Context.Set<ApprovalTask>().AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_ChangedAfterSubmit_AffectsOnlyLaterStages()
    {
        var r = await RigWithManagerFlow(stages: 2);
        var id = InstanceOf(await SubmitAs(r, "gm"));
        OpenAssignees(id).Should().Equal("admin");

        await SetOverride(r, $"group:{FinanceGroup}");
        TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open).AssigneeUserId.Should().Be("admin");   // 已建立的任務不變

        (await Approve(id, "admin")).IsSuccess.Should().BeTrue();
        OpenAssignees(id).Should().Equal("fin1", "fin2");   // 下一關用新值
    }

    [Fact]
    public async Task Fallback_RematchFallbackInstance_UsesPolicy()
    {
        var r = await Scenario3();
        await SetOverride(r, $"group:{FinanceGroup}");
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        // 簽核中追加負費用讓金額掉到沒有流程符合 → 重新比對產生備援實例
        As("buyer1");
        (await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(r.Extra,
            V((TitleId(r.Extra), "追加"), (r.ExtraRef, po.Id), (r.ExtraAmount, -45000))))).IsCreated.Should().BeTrue();

        (await Approve(id, "buyer2")).IsSuccess.Should().BeTrue();
        var fresh = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == po.Id && i.Status == ApprovalInstanceStatus.Pending);
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);
        OpenAssignees(fresh.Id).Should().Equal("fin1", "fin2");
    }

    /// <summary>簽核中把已確認的轉移拿掉，執行期再驗失敗、系統管理員收到異常通知。</summary>
    private async Task TriggerAlert()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var wf = Db.Context.Set<Workflow>().Single(w => w.ModuleId == r.ModuleId);
        wf.Transitions = [.. wf.Transitions.Where(x => !(x.From == r.Draft && x.To == r.Confirmed))];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await Approve(id, "buyer2")).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Alert_Factory_AllAdmins() { await TriggerAlert(); Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1); }

    [Fact]
    public async Task Alert_SystemRecipientsUser_NotifiesOnlyThatUser()
    {
        await SetSystemAlert("user:fin1");
        await TriggerAlert();
        Notices("fin1", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(0);
    }

    [Fact]
    public async Task Alert_OnlyDisabledRecipient_FallsBackToAdmins()
    {
        await SetSystemAlert("user:fin1");
        await Disable("fin1");
        await TriggerAlert();
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
        Notices("fin1", NotificationKinds.ApprovalAdminAlert).Should().Be(0);
        _logs.Should().Contain(l => l.Level == LogLevel.Warning);
    }
}
