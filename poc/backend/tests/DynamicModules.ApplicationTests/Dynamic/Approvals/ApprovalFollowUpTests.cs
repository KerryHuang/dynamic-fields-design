using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>a2a 審查的後續（主控裁決 17、18 與小修）。</summary>
public class ApprovalFollowUpTests : ApprovalTestBase
{
    [Fact]
    public async Task FlowKey_Order_Reserved_400()
    {
        var r = await BuildPurchase();
        var res = await Send(new CreateApprovalFlowRequest(r.Module,
            new ApprovalFlowInput("order", Text("order"), r.Confirmed, 1, true, Def(null, Stage("mgr", Manager(1))))));
        res.Errors.Should().ContainKey("key");
        res.Errors["key"].Should().Contain(ApprovalMessages.FlowKeyReserved);
    }

    [Fact]
    public async Task SaveWorkflow_NullTransitions_TreatedAsEmpty()
    {
        var r = await BuildPurchase();
        var res = await Send(new SaveWorkflowRequest(r.Module, new WorkflowInput(r.Status, r.Draft, AllStates(r), null!)));
        res.IsSuccess.Should().BeTrue(string.Join("；", res.Errors.Select(e => $"{e.Key}={e.Value[0]}")));
    }

    [Fact]
    public async Task IntegrationReactivate_EventPushingApproval_StaysInactive()
    {
        var r = await BuildPurchase();
        var push = Event("po-push", r.Extra, new EventTrigger("create", null, null, null),
            UpdateVia("touch", r.Module, r.ExtraRef, Const(r.Status, $"\"{Opt(r.Confirmed)}\"")));
        var benign = Event("po-memo", r.Extra, new EventTrigger("create", null, null, null),
            UpdateVia("memo", r.Module, r.ExtraRef, Const(r.Memo, "\"x\"")));
        foreach (var e in new[] { push, benign })
        {
            var created = await Send(new CreateEventRequest(e));
            created.IsCreated.Should().BeTrue(string.Join("；", created.Errors.Select(x => $"{x.Key}={x.Value[0]}")));
        }
        // 整合暫停：事件被停用；此時把 草稿 → 已確認 開需簽核（只看啟用中的事件，所以通過）
        await Db.Context.Set<ModuleEvent>().ExecuteUpdateAsync(s => s.SetProperty(e => e.Active, false));
        await SeedFlow(r, "po-b", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)]);
        wf.IsSuccess.Should().BeTrue(string.Join("；", wf.Errors.Select(x => $"{x.Key}={x.Value[0]}")));

        AppEvent Decl(string code) => new(code, Text(code), true, r.Extra, new AppEventTrigger("create", null, null, null), null, []);
        var decl = new AppIntegrationDecl("link", Text("link"), [], "block", null, [Decl("po-push"), Decl("po-memo")], null);
        var blocked = await Svc<IntegrationReconciler>().SetActiveAsync(
            new IntegrationItems(null, ["po-push", "po-memo"], null), decl, true, CancellationToken.None);

        blocked.Should().Equal("po-push");
        Db.Context.ChangeTracker.Clear();
        var active = Db.Context.Set<ModuleEvent>().AsNoTracking().ToDictionary(e => e.Key, e => e.Active);
        active["po-push"].Should().BeFalse();
        active["po-memo"].Should().BeTrue();
    }
}
