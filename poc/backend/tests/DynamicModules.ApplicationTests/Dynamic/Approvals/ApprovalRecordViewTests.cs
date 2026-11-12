using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>記錄回應的簽核鎖定欄位與臨時讀取旗標（a4b 計畫 J2、J3）。</summary>
public class ApprovalRecordViewTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _files;

    /// <summary>草稿 → 已確認 需簽核（流程條件引用折扣後總額）；單據多一個附件欄位；採購主管群組（buyer2）簽核。</summary>
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _files = (await MustAdd(_r.Module, FieldType.Attachment, label: "附件")).Id;
        await SeedFlow(_r, "po-a", _r.Confirmed, 1, Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(_r.Module, _r.Extra);
    }

    private async Task<(long RecordId, long InstanceId)> Pending()
    {
        As("buyer1");
        var po = await NewPo(_r, extra: V((_r.Discount, 0)));
        return (po.Id, (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value);
    }

    private async Task<long> Confirmed()
    {
        var (po, instance) = await Pending();
        As("buyer2");
        var task = TasksOf(instance).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instance).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        StateOf(_r, po).Should().Be(_r.Confirmed);
        return po;
    }

    private async Task<DynamicRecordResponse> Get(string user, long id)
    {
        As(user);
        var res = await Send(new GetRecordRequest(id));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (DynamicRecordResponse)res.Data!;
    }

    [Fact]
    public async Task GetRecord_LockedState_ListsLockedFieldKeys()
    {
        await Rig();
        var po = await Confirmed();

        var body = await Get("emp1", po);

        body.ApprovalLock.Should().NotBeNull();
        body.ApprovalLock!.FieldKeys.Should().Contain([$"f_{_r.NetTotal}", $"f_{_r.Discount}", $"f_{_files}"]);
        body.ApprovalLock.FieldKeys.Should().NotContain([$"f_{_r.Memo}", $"f_{_r.Status}"]);
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, true, CancellationToken.None))!;
        var expected = await sp.GetRequiredService<ApprovalLockService>().ForModuleAsync(module, null, CancellationToken.None);
        body.ApprovalLock.DetailRowsLocked.Should().Be(expected.DetailRowsLocked);
        body.ApprovalLock.FieldKeys.Should().BeEquivalentTo(
            module.Blocks.SelectMany(b => b.Fields).Where(f => expected.FieldIds.Contains(f.Id)).Select(f => f.Key));
    }

    [Fact]
    public async Task GetRecord_Draft_ApprovalLockNull()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r);
        (await Get("emp1", po.Id)).ApprovalLock.Should().BeNull();
    }

    [Fact]
    public async Task GetRecord_Pending_ApprovalLockNull()
    {
        await Rig();
        var (po, _) = await Pending();
        (await Get("emp1", po)).ApprovalLock.Should().BeNull();
    }

    [Fact]
    public async Task GetRecord_LockedFieldHiddenForReader_NotListed()
    {
        await Rig();
        var po = await Confirmed();
        As("admin");
        await HideFor(_r.Module, _r.Discount, Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id);

        var body = await Get("emp1", po);

        body.ApprovalLock!.FieldKeys.Should().NotContain($"f_{_r.Discount}").And.Contain($"f_{_files}");
    }

    [Fact]
    public async Task GetRecord_ViaApprovalGrant_FlagTrue()
    {
        await Rig();
        var (po, _) = await Pending();
        As("admin");
        await RevokeAll(_r.Module);

        var body = await Get("buyer2", po);

        body.ApprovalGrant.Should().BeTrue();
    }

    [Fact]
    public async Task GetRecord_WithRead_FlagFalse()
    {
        await Rig();
        var (po, _) = await Pending();
        (await Get("emp1", po)).ApprovalGrant.Should().BeFalse();
    }
}
