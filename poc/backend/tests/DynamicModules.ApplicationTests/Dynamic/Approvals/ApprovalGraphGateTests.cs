using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// graph 執行入口開關（計畫 fg2 J1）：正式 DI 組態下一律關閉、graph 照 fg1 回 409；只有測試主機能換成開啟版。
/// 這個類別的測試主機沒有呼叫 <see cref="ApprovalGraphTestKit.OpenGate"/>，等於正式組態。
/// </summary>
public class ApprovalGraphGateTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static FlowSnapshotEntry Entry(ApprovalDefinition d) => new(1, 1, 1, true, Text("po-g"), d);

    private static ApprovalDefinition SequenceDef() =>
        new(ApprovalShapes.Sequence, null, null, null, [Stage("lead", Group(1))]);

    private static ApprovalDefinition Linear(ApproverSource who) =>
        G([Start(), Approval("mgr", who), End()], [E("e1", "start", "mgr"), E("e2", "mgr", "end")]);

    // ───────── 開關本身 ─────────

    [Fact]
    public void Gate_DefaultRegistration_IsClosed()
    {
        // 從正式的 AddApplication() 解析（不經測試基底），而且註冊的就是 ApprovalShapeGate 本身，不是會開的子類
        using var sp = new ServiceCollection().AddApplication().BuildServiceProvider();

        var gate = sp.GetRequiredService<ApprovalShapeGate>();

        gate.Open.Should().BeFalse();
        gate.GetType().Should().Be(typeof(ApprovalShapeGate));
        sp.GetRequiredService<ApprovalShapeGate>().Should().BeSameAs(gate, "單例");
    }

    [Fact]
    public void Gate_TestHostWithoutOpenGate_IsClosed()
    {
        Svc<ApprovalShapeGate>().Open.Should().BeFalse();
    }

    [Fact]
    public void Gate_Opened_RejectReturnsNull()
    {
        var services = new ServiceCollection().AddApplication();
        OpenGate(services);
        using var sp = services.BuildServiceProvider();

        var gate = sp.GetRequiredService<ApprovalShapeGate>();

        gate.Open.Should().BeTrue();
        gate.Reject(Entry(Linear(Manager(1)))).Should().BeNull();
    }

    [Fact]
    public void Gate_OpenGateOnOneHost_DoesNotLeakIntoProductionRegistration()
    {
        var opened = new ServiceCollection().AddApplication();
        OpenGate(opened);
        using var openedSp = opened.BuildServiceProvider();
        using var productionSp = new ServiceCollection().AddApplication().BuildServiceProvider();

        openedSp.GetRequiredService<ApprovalShapeGate>().Open.Should().BeTrue();
        productionSp.GetRequiredService<ApprovalShapeGate>().Open.Should().BeFalse();
    }

    [Fact]
    public void Gate_Closed_RejectsGraphAndPassesSequence()
    {
        var gate = Svc<ApprovalShapeGate>();

        var res = gate.Reject(Entry(Linear(Manager(1))));

        res.Should().NotBeNull();
        res!.IsConflict.Should().BeTrue();
        res.Message.Should().Be(ApprovalMessages.GraphNotSupported("po-g").Resolve(null));
        gate.Reject(Entry(SequenceDef())).Should().BeNull();
        gate.Reject(null).Should().BeNull();
    }

    // ───────── 測輔助 ─────────

    [Fact]
    public void KitBuilds_DesignExample_PassesValidator()
    {
        // 流程圖設計 §7.1 的範例（含通知節點）；欄位與群組代號是假的，這裡只驗結構
        var def = G(
            [Start(), Approval("mgr", Manager(1)), Condition("amount"), Fork("split"), Approval("legal", Group(5)), Approval("fin", Group(6), editable: [31]),
             Join("merge", "split"), Notify("tell-hr"), End()],
            [E("e1", "start", "mgr"), E("e2", "mgr", "amount"),
             E("e3", "amount", "split", Cond(21, "gte", 100000), order: 1), E("e4", "amount", "tell-hr", isDefault: true),
             E("e5", "split", "legal"), E("e6", "split", "fin"), E("e7", "legal", "merge"), E("e8", "fin", "merge"),
             E("e9", "merge", "tell-hr"), E("e10", "tell-hr", "end")]);

        var check = ApprovalGraphValidator.Check(def, new GraphPolicyLimits(int.MaxValue, int.MaxValue), previous: null);

        check.Errors.Should().BeEmpty();
        def.Shape.Should().Be(ApprovalShapes.Graph);
    }

    // ───────── 正式組態下 graph 的四個入口照舊 409（fg1 行為） ─────────

    private async Task<PurchaseRig> WithGraphFlow()
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, Linear(Manager(1)));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial),
            Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    [Fact]
    public async Task Submit_ValidGraphFlow_ProductionDi_409_NothingWritten()
    {
        // 結構完全合法的 graph 也一樣擋：擋門看形狀，不看結構
        var r = await WithGraphFlow();
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var rowVersion = Rec(po.Id).Row.RowVersion;

        var res = await Submit(po.Id, r.Confirmed);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphNotSupported("po-g").Resolve(null));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalTask>().AsNoTracking().Count().Should().Be(0);
        (StateOf(r, po.Id), Rec(po.Id).Row.RowVersion).Should().Be((r.Draft, rowVersion));
    }

    [Fact]
    public async Task Preview_ValidGraphFlow_ProductionDi_409()
    {
        var r = await WithGraphFlow();
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphNotSupported("po-g").Resolve(null));
    }
}
