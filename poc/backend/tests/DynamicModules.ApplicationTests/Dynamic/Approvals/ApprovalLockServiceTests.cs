using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核鎖定的單一來源（設計 §9.3）：鎖定狀態、鎖定欄位、明細鎖。</summary>
public class ApprovalLockServiceTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<ApprovalLock> LockOf(string module, Func<Module, Workflow?>? candidate = null, IServiceProvider? sp = null)
    {
        using var scope = NewScope();
        sp ??= scope.ServiceProvider;
        var m = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(module, includeChildren: true, CancellationToken.None))!;
        return await sp.GetRequiredService<ApprovalLockService>().ForModuleAsync(m, candidate?.Invoke(m), CancellationToken.None);
    }

    /// <summary>草稿 → 已確認 需簽核，流程條件引用 field。</summary>
    private async Task<PurchaseRig> RigWith(Func<PurchaseRig, long> conditionField, string key = "po")
    {
        var r = await BuildPurchase(key);
        await SeedFlow(r, $"{key}-a", r.Confirmed, 1, Def(Cond(conditionField(r), "eq", 1), Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        return r;
    }

    [Fact]
    public async Task ForModule_NoApprovalTransition_ReturnsNone()
    {
        var r = await BuildPurchase();   // Plain 狀態機

        (await LockOf(r.Module)).Should().BeSameAs(ApprovalLock.None);
    }

    [Fact]
    public async Task ForModule_Scenario3_LockedStatesMatchTopology()
    {
        var r = await Scenario3();
        using var scope = NewScope();
        var wf = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, true, CancellationToken.None))!.Workflow!;

        var l = await LockOf(r.Module);

        l.LockedStateIds.Should().BeEquivalentTo(ApprovalTopology.LockedStates(wf)).And.Contain([r.Confirmed, r.Cancelled]);
        l.FieldIds.Should().Contain([r.Total, r.LineSum, r.ExtraSum]);
    }

    [Fact]
    public async Task ForModule_DetailRollupFromLine_DetailRowsLocked()
    {
        var net = await RigWith(r => r.NetTotal, "pa");
        var supplier = await RigWith(r => r.Supplier, "pb");

        (await LockOf(net.Module)).DetailRowsLocked.Should().BeTrue();
        (await LockOf(supplier.Module)).DetailRowsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task ForModule_RollupViaOtherReferenceToHeader_NotOwnDetail()
    {
        var r = await BuildPurchase();
        // 明細模組另有一個指回表頭的一般參照；經那個參照做的彙總不算本單明細
        var alt = await MustAdd(r.Line, FieldType.Reference, RefTo(r.Module), "另一個表頭");
        var viaAlt = await MustAdd(r.Module, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(alt.Id, RollupFunction.Sum, r.LineSubtotal, null) }, "經另一參照的合計");
        await SeedFlow(r, "po-alt", r.Confirmed, 1, Def(Cond(viaAlt.Id, "eq", 1), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial)])).IsSuccess.Should().BeTrue();

        var l = await LockOf(r.Module);

        l.FieldIds.Should().Contain(viaAlt.Id);
        l.DetailRowsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task ForModule_CandidateWorkflow_UsesCandidate()
    {
        var r = await RigWith(x => x.Supplier);
        using var scope = NewScope();   // 同一個 scope：第二次呼叫才會碰到第一次的快取
        (await LockOf(r.Module, null, scope.ServiceProvider)).LockedStateIds.Should().NotBeEmpty();   // 先以 null 算一次（寫入快取）

        var l = await LockOf(r.Module, m => new Workflow
        {
            StatusFieldId = r.Status, InitialOptionId = r.Draft, States = m.Workflow!.States,
            Transitions = [Plain(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial)]
        }, scope.ServiceProvider);

        l.LockedStateIds.Should().BeEmpty();   // 有快取時傳候選也不能讀到快取
        (await LockOf(r.Module, null, scope.ServiceProvider)).LockedStateIds.Should().NotBeEmpty();   // 候選的結果也不能寫進快取

    }

    [Fact]
    public async Task Reset_AfterWorkflowSaved_RecomputesInSameScope()
    {
        var r = await BuildPurchase();   // Plain 狀態機
        using var scope = NewScope();   // 長 scope（Demo 產生器）：結構在 scope 內才存好
        (await LockOf(r.Module, null, scope.ServiceProvider)).Should().BeSameAs(ApprovalLock.None);   // 快取成 None
        await SeedFlow(r, "po-r", r.Confirmed, 1, Def(Cond(r.Supplier, "eq", 1), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial)])).IsSuccess.Should().BeTrue();
        (await LockOf(r.Module, null, scope.ServiceProvider)).Should().BeSameAs(ApprovalLock.None);   // 沒清就還是舊的

        scope.ServiceProvider.GetRequiredService<ApprovalLockService>().Reset();

        (await LockOf(r.Module, null, scope.ServiceProvider)).LockedStateIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task WorkflowResponse_LockedFieldIds_IncludesAttachmentField()
    {
        var r = await BuildPurchase();
        var files = await MustAdd(r.Module, FieldType.Attachment, label: "附件");
        await SeedFlow(r, "po-f", r.Confirmed, 1, Def(Cond(r.Supplier, "eq", 1), Stage("lead", Group(PurchaseLeadGroup))));

        var res = await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial)]);

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((WorkflowResponse)res.Data!).LockedFieldIds.Should().Contain([r.Supplier, files.Id]);
    }
}
