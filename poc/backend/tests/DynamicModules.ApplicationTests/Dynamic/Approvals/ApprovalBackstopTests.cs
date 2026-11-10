using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核中的寫入後盾與需簽核轉移的入口（設計 §5.3、§8.5）；pending 實例直接放在 Db。</summary>
public class ApprovalBackstopTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private ApprovalFlow _flow = null!;

    /// <summary>草稿 → 已確認 需簽核（block），另有一般轉移 草稿 → 已退回。</summary>
    private async Task<PurchaseRig> Rig()
    {
        _r = await BuildPurchase();
        _flow = await SeedFlow(_r, "po-b", _r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled), Plain(_r.Draft, _r.Returned)]);
        wf.IsSuccess.Should().BeTrue(string.Join("；", wf.Errors.Select(e => $"{e.Key}={e.Value[0]}")));
        return _r;
    }

    private async Task<DynamicRecordResponse> PendingPo(Dictionary<string, JsonElement?>? extra = null)
    {
        var po = await NewPo(_r, extra: extra);
        await SeedInstance(_r, _flow, recordId: po.Id);
        return po;
    }

    private string Status => $"f_{_r.Status}";
    private static JsonElement? O(long id) => JsonSerializer.SerializeToElement($"opt_{id}");

    [Fact]
    public async Task Put_Pending_409_And_Put_StatusToApprovalTarget_400_EvenWhenPending()
    {
        await Rig();
        var po = await PendingPo();

        var memo = await Put(po.Id, V((_r.Memo, "改")));
        memo.IsConflict.Should().BeTrue();
        memo.Message.Should().Be("簽核中，不可修改");

        var status = await Put(po.Id, new() { [Status] = O(_r.Confirmed) });
        status.Errors[Status].Should().Equal("這個狀態需要送出簽核");

        var other = await Put(po.Id, new() { [Status] = O(_r.Returned) });
        other.IsConflict.Should().BeTrue();
        other.Message.Should().Be(ApprovalMessages.WithdrawFirst);
    }

    [Fact]
    public async Task Put_StatusToApprovalTarget_AsAdmin_400()
    {
        await Rig();
        var po = await NewPo(_r);   // 目前使用者是系統管理員，也不能用 PUT 走需簽核轉移
        var res = await Put(po.Id, new() { [Status] = O(_r.Confirmed) });
        res.Errors[Status].Should().Equal(ApprovalMessages.NeedsSubmit);
        StateOf(_r, po.Id).Should().Be(_r.Draft);
        // 一般轉移照舊
        (await Put(po.Id, new() { [Status] = O(_r.Returned) })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Pending_409()
    {
        await Rig();
        var po = await PendingPo();
        var res = await Send(new DeleteRecordRequest(po.Id, Rec(po.Id).Row.RowVersion));
        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be(ApprovalMessages.Pending);
        Rec(po.Id).Row.DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task DetailRowEdit_Pending_409()
    {
        await Rig();
        var po = await PendingPo();
        var row = new DetailRowInput(null, V(((await Detail(_r.Line)).TitleFieldId!.Value, "列"), (_r.LineQty, 5), (_r.LinePrice, 10)), null);
        var res = await Put(po.Id, [], new() { [_r.Line] = [row] });
        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be(ApprovalMessages.Pending);
    }

    [Fact]
    public async Task EventUpdate_OnPendingRecord_Rollback409()
    {
        await Rig();
        var created = await Send(new CreateEventRequest(Event("po-touch", _r.Extra, new EventTrigger("create", null, null, null),
            UpdateVia("memo", _r.Module, _r.ExtraRef, Const(_r.Memo, "\"x\"")))));
        created.IsCreated.Should().BeTrue(string.Join("；", created.Errors.Select(e => $"{e.Key}={e.Value[0]}")));
        var po = await PendingPo();
        var before = Rec(po.Id).Row.ValuesJson;

        var res = await NewExtra(po.Id, 100);
        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be(ApprovalMessages.EventBlocked(po.Id));
        Rec(po.Id).Row.ValuesJson.Should().Be(before);
        Db.Context.Set<Record>().AsNoTracking().Count(x => x.ModuleId == ExtraModuleId()).Should().Be(0);
    }

    [Fact]
    public async Task ReferenceSetNull_OnPendingRecord_409()
    {
        await Rig();
        var vendorRef = await MustAdd(_r.Module, FieldType.Reference, RefTo(_r.Vendor, onDelete: ReferenceOnDelete.SetNull), "備用供應商");
        var vendorTitle = (await Detail(_r.Vendor)).TitleFieldId!.Value;
        var vendor = (DynamicRecordResponse)(await Send(new CreateRecordRequest(_r.Vendor, V((vendorTitle, "v1"))))).Data!;
        var po = await PendingPo(V((vendorRef.Id, vendor.Id)));

        var res = await Send(new DeleteRecordRequest(vendor.Id, Rec(vendor.Id).Row.RowVersion));
        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be(ApprovalMessages.EventBlocked(po.Id));
        Rec(vendor.Id).Row.DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task RollupFromIndependentModule_Allowed()
    {
        await Rig();
        var po = await PendingPo();
        var res = await NewExtra(po.Id, 250);
        res.IsCreated.Should().BeTrue(res.Message);
        Rec(po.Id).Values[$"f_{_r.ExtraSum}"]!.Value.GetDecimal().Should().Be(250);
    }

    [Fact]
    public async Task Runner_ExemptOnlyOwnRecordAndKeys()
    {
        await Rig();
        var po = await NewPo(_r);
        var inst = await SeedInstance(_r, _flow, recordId: po.Id);

        // 豁免 A 的狀態鍵；同一請求以系統身分改 A 的備註 → 409
        (await RunInOneRequest(inst.Id, po.Id, alsoMemo: true)).Should().Match<ServiceResult?>(x => x != null && x.IsConflict);
        // 只走豁免的狀態鍵 → 放行
        var other = await NewPo(_r);
        var inst2 = await SeedInstance(_r, _flow, recordId: other.Id);
        (await RunInOneRequest(inst2.Id, other.Id, alsoMemo: false)).Should().BeNull();
    }

    [Fact]
    public async Task SystemPushToApprovalTarget_WithoutExempt_409()
    {
        await Rig();
        var po = await NewPo(_r);
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var update = sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>();
        using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
            (await update.Handle(new UpdateRecordRequest(po.Id, new() { [Status] = O(_r.Confirmed) }, Rec(po.Id).Row.RowVersion, null,
                new HashSet<string> { Status }), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        var res = await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None);
        res!.Message.Should().Be(ApprovalMessages.TransitionBlocked(po.Id));
    }

    /// <summary>同一個 scope 內以系統身分依序改狀態（每步各自 commit，同一份寫入日誌），最後跑後盾。</summary>
    private async Task<ServiceResult?> SystemSteps(long recordId, params long[] states)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var update = sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>();
        foreach (var to in states)
        {
            var row = await sp.GetRequiredService<IRecordRepository>().GetAsync(recordId, CancellationToken.None);
            using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
                (await update.Handle(new UpdateRecordRequest(recordId, new() { [Status] = O(to) }, row!.RowVersion, null,
                    new HashSet<string> { Status }), CancellationToken.None)).IsSuccess.Should().BeTrue();
            await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        }
        return await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SystemChain_PlainThenApprovalTransition_InOneRequest_409()
    {
        // 草稿 → 已退回（一般）→ 已取消（需簽核）：合併後只看得到「草稿 → 已取消」這條不存在的轉移，要逐筆看（審查建議 1）
        _r = await BuildPurchase();
        _flow = await SeedFlow(_r, "po-b", _r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedFlow(_r, "po-c", _r.Cancelled, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Needs(_r.Confirmed, _r.Cancelled), Plain(_r.Draft, _r.Returned), Needs(_r.Returned, _r.Cancelled)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        var po = await NewPo(_r);
        (await SystemSteps(po.Id, _r.Returned, _r.Cancelled))!.Message.Should().Be(ApprovalMessages.TransitionBlocked(po.Id));
    }

    [Fact]
    public async Task Runner_ExemptChecksValue_LaterWriteMovesStatusAgain_409()
    {
        // 豁免只放行簽核自己寫的值：同一請求裡狀態又被推到別處（已確認 → 部分到貨）→ 409（審查建議 1）
        await Rig();
        var po = await NewPo(_r);
        var inst = await SeedInstance(_r, _flow, recordId: po.Id);
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var loaded = (await sp.GetRequiredService<IApprovalInstanceRepository>().GetAsync(inst.Id, CancellationToken.None))!;
        (await sp.GetRequiredService<ApprovalTransitionRunner>().RunAsync(loaded, _r.Confirmed, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        var row = await sp.GetRequiredService<IRecordRepository>().GetAsync(po.Id, CancellationToken.None);
        using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
            (await sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>().Handle(new UpdateRecordRequest(po.Id,
                new() { [Status] = O(_r.Partial) }, row!.RowVersion, null, new HashSet<string> { Status }), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        (await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None))!.Message.Should().Be(ApprovalMessages.EventBlocked(po.Id));
    }

    private async Task<ServiceResult?> RunInOneRequest(long instanceId, long recordId, bool alsoMemo)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var inst = (await sp.GetRequiredService<IApprovalInstanceRepository>().GetAsync(instanceId, CancellationToken.None))!;
        (await sp.GetRequiredService<ApprovalTransitionRunner>().RunAsync(inst, _r.Confirmed, null, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        if (alsoMemo)
        {
            var update = sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>();
            var row = await sp.GetRequiredService<IRecordRepository>().GetAsync(recordId, CancellationToken.None);
            using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
                (await update.Handle(new UpdateRecordRequest(recordId, V((_r.Memo, "改")), row!.RowVersion, null,
                    new HashSet<string> { $"f_{_r.Memo}" }), CancellationToken.None)).IsSuccess.Should().BeTrue();
            await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        }
        return await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None);
    }

    private long ExtraModuleId() => Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Extra).Id;

    private async Task<ServiceResult> NewExtra(long poId, decimal amount)
    {
        var title = (await Detail(_r.Extra)).TitleFieldId!.Value;
        return await Send(new CreateRecordRequest(_r.Extra, V((title, "追加"), (_r.ExtraRef, poId), (_r.ExtraAmount, amount))));
    }
}
