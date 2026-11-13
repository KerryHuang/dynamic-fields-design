using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>A15：系統管理員是否略過狀態轉移上的群組限制（approval.adminBypassTransitionGroups，全系統，出廠 true）。</summary>
public class TransitionAccessTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    /// <summary>草稿 → 已確認（需簽核、無流程直接轉移）與 已確認 → 部分進貨，都限定採購主管群組。</summary>
    private async Task<PurchaseRig> Rig()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-never", r.Confirmed, 1, Def(Cond(r.Total, "gte", 999999999), Stage("m", Manager(1))));   // 永遠不符合 → 直接轉移
        var wf = await SaveWorkflow(r, [
            new WorkflowTransition(r.Draft, r.Confirmed, [PurchaseLeadGroup], new TransitionApproval(true, ApprovalNoMatch.Direct)),
            new WorkflowTransition(r.Confirmed, r.Partial, [PurchaseLeadGroup]),
            Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)
        ]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(r.Module);
        Db.Context.Add(new AppUser { UserId = "boss", DisplayName = "boss", IsSystemAdmin = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return r;
    }

    private async Task PolicyOff()
    {
        await Svc<ISystemSettingRepository>().SetAsync(PolicyCatalog.ApprovalAdminBypassTransitionGroups.SettingKey, "false", default);
        await Db.Context.SaveChangesAsync();
    }

    /// <summary>採購主管 buyer2 建單並送出，直接轉成已確認。</summary>
    private async Task<long> ConfirmedPo(PurchaseRig r)
    {
        As("buyer2");
        var po = await NewPo(r, price: 100);
        (await Submit(po.Id, r.Confirmed)).IsSuccess.Should().BeTrue();
        StateOf(r, po.Id).Should().Be(r.Confirmed);
        return po.Id;
    }

    private Task<ServiceResult> Move(PurchaseRig r, long id, long to) => Put(id, new() { [$"f_{r.Status}"] = JsonSerializer.SerializeToElement($"opt_{to}") });

    [Fact]
    public async Task Admin_Factory_BypassesTransitionGroups()
    {
        var r = await Rig();
        var id = await ConfirmedPo(r);
        As("boss");
        var res = await Move(r, id, r.Partial);
        res.IsSuccess.Should().BeTrue(Dump(res));
        As("boss");
        var draft = await NewPo(r, price: 100);
        (await Submit(draft.Id, r.Confirmed)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_PolicyOff_RespectsTransitionGroups()
    {
        var r = await Rig();
        var id = await ConfirmedPo(r);
        await PolicyOff();
        As("boss");
        var res = await Move(r, id, r.Partial);
        res.IsSuccess.Should().BeFalse();
        Err(res, $"f_{r.Status}").Should().Be(WorkflowEnforcer.NotPermittedMessage);
        StateOf(r, id).Should().Be(r.Confirmed);
        // 轉移群組內的人照常可以
        As("buyer2");
        (await Move(r, id, r.Partial)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_PolicyOff_SubmitRespectsGroups()
    {
        var r = await Rig();
        await PolicyOff();
        As("boss");
        var po = await NewPo(r, price: 100);
        var res = await Submit(po.Id, r.Confirmed);
        res.IsSuccess.Should().BeFalse();
        Err(res, $"f_{r.Status}").Should().NotBeNullOrEmpty();
        StateOf(r, po.Id).Should().Be(r.Draft);
    }

    [Fact]
    public async Task Admin_PolicyOff_SubmitTargetsExcludeRestricted()
    {
        var r = await Rig();
        As("boss");
        var po = await NewPo(r, price: 100);
        async Task<List<long>> Targets()
        {
            using var scope = NewScope();
            var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, true, default))!;
            var record = (await scope.ServiceProvider.GetRequiredService<IRecordRepository>().GetAsync(po.Id, default))!;
            return await scope.ServiceProvider.GetRequiredService<ApprovalReader>().SubmitTargetsAsync(module, record, false, default);
        }
        (await Targets()).Should().Equal(r.Confirmed);
        await PolicyOff();
        (await Targets()).Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_PolicyOff_PreviewRespectsGroups()
    {
        var r = await Rig();
        As("boss");
        var po = await NewPo(r, price: 100);
        (await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed))).IsSuccess.Should().BeTrue();
        await PolicyOff();
        (await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Admin_PolicyOff_StillHasModulePermissions()
    {
        var r = await Rig();
        var id = await ConfirmedPo(r);
        await PolicyOff();
        As("boss");
        var res = await Put(id, V((r.Memo, "備註")));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Admin_PolicyOff_PermittedTransitionsFollowGroups()
    {
        var r = await Rig();
        await PolicyOff();
        As("boss");
        using var scope = NewScope();
        var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, true, default))!;
        var perms = (await scope.ServiceProvider.GetRequiredService<IDynamicAccessGuard>().AuthorizeAsync(module, PermissionAction.Edit, default)).Permissions!;
        var allowed = await scope.ServiceProvider.GetRequiredService<WorkflowEnforcer>().PermittedTransitionsAsync(module, perms, default);
        allowed!.Select(t => (t.From, t.To)).Should().BeEquivalentTo([(r.Partial, r.Received), (r.Confirmed, r.Cancelled)]);
    }

    [Fact]
    public async Task SystemIdentity_PolicyOff_StillBypasses()
    {
        var r = await Rig();
        var id = await ConfirmedPo(r);
        await PolicyOff();
        As("emp1");   // 不在轉移群組，也不是系統管理員
        using var scope = NewScope();
        var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(r.Module, true, default))!;
        var previous = Rec(id).Values;
        var next = new Dictionary<string, JsonElement?>(previous) { [$"f_{r.Status}"] = JsonSerializer.SerializeToElement($"opt_{r.Partial}") };
        var enforcer = scope.ServiceProvider.GetRequiredService<WorkflowEnforcer>();
        (await enforcer.CheckAsync(module, previous, next, default)).Should().NotBeEmpty();   // 一般使用者被擋（對照）
        using (scope.ServiceProvider.GetRequiredService<ISystemIdentity>().Enter(null))
            (await enforcer.CheckAsync(module, previous, next, default)).Should().BeEmpty();
    }
}
