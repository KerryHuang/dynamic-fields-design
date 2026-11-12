using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核鎖定狀態的寫入規則（設計 §8.5、§9.3）：第一道保留計算欄位，第二道（後盾）擋鎖定欄位與明細。</summary>
public class ApprovalLockTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;

    private string Status => $"f_{_r.Status}";
    private static JsonElement? O(long id) => JsonSerializer.SerializeToElement($"opt_{id}");

    /// <summary>
    /// 草稿 → 已確認 需簽核（流程條件引用折扣後總額，所以總額、明細合計、追加費用合計、折扣都鎖；交期是簽核人可編輯欄位也鎖；備註不鎖）。
    /// 已確認 → 草稿 是解鎖轉移；已確認 → 部分進貨 一般；已確認 → 已取消 需簽核（作廢）。
    /// </summary>
    private async Task<PurchaseRig> Rig(Func<PurchaseRig, Task<long>>? onlyCondition = null)
    {
        _r = await BuildPurchase();
        // onlyCondition：流程只引用這個欄位（放在一個永遠跳過的關卡條件裡），用來測「明細不鎖」「參照 SetNull」
        var def = onlyCondition is null
            ? Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup), editable: [_r.DueDate]))
            : Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [_r.DueDate]),
                Stage("sup", Group(PurchaseLeadGroup), Cond(await onlyCondition(_r), "eq", 987654321)));
        await SeedFlow(_r, "po-a", _r.Confirmed, 1, def);
        await SeedFlow(_r, "po-x", _r.Cancelled, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial),
            Plain(_r.Partial, _r.Received), Needs(_r.Confirmed, _r.Cancelled)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(_r.Module, _r.Extra);
        return _r;
    }

    /// <summary>建單、送簽、簽核人同意，回到管理員身分；單據停在已確認。</summary>
    private async Task<DynamicRecordResponse> ApprovedPo(Func<PurchaseRig, Task<long>>? onlyCondition = null,
        Func<PurchaseRig, Dictionary<string, JsonElement?>>? extra = null)
    {
        await Rig(onlyCondition);
        As("buyer1");
        var values = V((_r.Discount, 0));
        foreach (var (k, v) in extra?.Invoke(_r) ?? []) values[k] = v;
        var po = await NewPo(_r, extra: values);
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        var res = await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
        As("admin");
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
        return po;
    }

    /// <summary>直接改資料庫的值，模擬「核准後改過公式」或其他讓儲存值與目前公式不同的情況。</summary>
    private async Task SetRaw(long recordId, long field, object value)
    {
        var row = Db.Context.Set<Record>().Single(x => x.Id == recordId);
        var values = RecordValueProcessor.Parse(row.ValuesJson);
        values[$"f_{field}"] = JsonSerializer.SerializeToElement(value);
        row.ValuesJson = RecordValueProcessor.Serialize(values);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private decimal Num(long recordId, long field) => Rec(recordId).Values[$"f_{field}"]!.Value.GetDecimal();

    private Record LineOf(long poId)
    {
        var lineModule = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Line).Id;
        return Db.Context.Set<Record>().AsNoTracking().Where(x => x.ModuleId == lineModule).AsEnumerable()
            .Single(x => Rec(x.Id).Values.TryGetValue($"f_{_r.LineHead}", out var h) && h is { } e && e.GetInt64() == poId);
    }

    // ───────── 第一道：鎖定記錄的計算欄位保留舊值 ─────────

    [Fact]
    public async Task Locked_StoredTotalDiffersFromFormula_PutMemo_TotalKept()
    {
        var po = await ApprovedPo();
        await SetRaw(po.Id, _r.NetTotal, 12345);

        var res = await Put(po.Id, V((_r.Memo, "x")));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Num(po.Id, _r.NetTotal).Should().Be(12345);
        Rec(po.Id).Values[$"f_{_r.Memo}"]!.Value.GetString().Should().Be("x");
    }

    [Fact]
    public async Task Locked_SystemUpdatesMemo_TotalKept()
    {
        // 事件、整合的更新經 EffectWriter 走同一個 UpdateRecordHandler，這裡直接以系統身分呼叫它
        var po = await ApprovedPo();
        await SetRaw(po.Id, _r.NetTotal, 12345);

        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
            (await sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>().Handle(new UpdateRecordRequest(po.Id,
                V((_r.Memo, "sys")), Rec(po.Id).Row.RowVersion, null, new HashSet<string> { $"f_{_r.Memo}" }), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);

        Num(po.Id, _r.NetTotal).Should().Be(12345);
    }

    [Fact]
    public async Task Locked_TransitionBackToDraft_FormulaRecomputed()
    {
        var po = await ApprovedPo();
        await SetRaw(po.Id, _r.NetTotal, 12345);

        var res = await Put(po.Id, new() { [Status] = O(_r.Draft) });

        res.IsSuccess.Should().BeTrue(Dump(res));
        StateOf(_r, po.Id).Should().Be(_r.Draft);
        Num(po.Id, _r.NetTotal).Should().Be(1000);
    }

    [Fact]
    public async Task Locked_PutHeaderWithUnchangedDetails_DetailComputedKept()
    {
        var po = await ApprovedPo();
        var line = LineOf(po.Id);
        await SetRaw(line.Id, _r.LineSubtotal, 777);
        line = LineOf(po.Id);
        var row = new DetailRowInput(line.Id, new Dictionary<string, JsonElement?>(), line.RowVersion);

        var res = await Put(po.Id, V((_r.Memo, "x")), new() { [_r.Line] = [row] });

        res.IsSuccess.Should().BeTrue(Dump(res));
        Num(LineOf(po.Id).Id, _r.LineSubtotal).Should().Be(777);
        Num(po.Id, _r.LineSum).Should().Be(1000);
    }

    [Fact]
    public async Task Draft_PutMemo_FormulaRecomputed()
    {
        await Rig();
        var po = await NewPo(_r, extra: V((_r.Discount, 0)));
        await SetRaw(po.Id, _r.NetTotal, 12345);

        (await Put(po.Id, V((_r.Memo, "x")))).IsSuccess.Should().BeTrue();

        Num(po.Id, _r.NetTotal).Should().Be(1000);
    }

    // ───────── 第二道：後盾 ─────────

    private Task<ServiceResult> NewExtra(long poId, decimal amount)
    {
        var title = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Extra).TitleFieldId!.Value;
        return Send(new CreateRecordRequest(_r.Extra, V((title, "追加"), (_r.ExtraRef, poId), (_r.ExtraAmount, amount))));
    }

    private static void ShouldRejectField(ServiceResult res, long field)
    {
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey($"f_{field}");
        res.Errors[$"f_{field}"][0].Should().Contain("這個欄位已經過簽核");
    }

    [Fact]
    public async Task Locked_PutLockedField_Returns400WithFieldKey()
    {
        var po = await ApprovedPo();

        ShouldRejectField(await Put(po.Id, V((_r.Discount, 50))), _r.Discount);
        Num(po.Id, _r.Discount).Should().Be(0);
    }

    [Fact]
    public async Task Locked_PutMemo_Succeeds()
    {
        var po = await ApprovedPo();

        (await Put(po.Id, V((_r.Memo, "x")))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Locked_PutStatusTransitionBackToDraft_Succeeds()
    {
        var po = await ApprovedPo();

        (await Put(po.Id, new() { [Status] = O(_r.Draft) })).IsSuccess.Should().BeTrue();
        StateOf(_r, po.Id).Should().Be(_r.Draft);
    }

    [Fact]
    public async Task Locked_TransitionBackAndChangeLockedField_SameRequest_Returns400()
    {
        var po = await ApprovedPo();

        var res = await Put(po.Id, new() { [Status] = O(_r.Draft), [$"f_{_r.Discount}"] = JsonSerializer.SerializeToElement(50) });

        ShouldRejectField(res, _r.Discount);
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
    }

    /// <summary>
    /// 在同一個 scope 依序做：使用者把記錄轉回草稿 → 輸入界線 → 事件效果（系統身分）改 Discount → 跑後盾。
    /// 與正式流程相同的順序（RecordEffectsBehavior 在 handler 之後、階段之前標記界線）。
    /// </summary>
    private async Task<ServiceResult?> UnlockThenSystemWrite(long poId, bool userAlsoChangesDiscount = false)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var update = sp.GetRequiredService<IRequestHandler<UpdateRecordRequest, ServiceResult>>();
        var values = new Dictionary<string, JsonElement?> { [Status] = O(_r.Draft) };
        if (userAlsoChangesDiscount) values[$"f_{_r.Discount}"] = JsonSerializer.SerializeToElement(9);
        (await update.Handle(new UpdateRecordRequest(poId, values, Rec(poId).Row.RowVersion, null), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        sp.GetRequiredService<IWriteJournal>().MarkInputEnd();
        var row = await sp.GetRequiredService<IRecordRepository>().GetAsync(poId, CancellationToken.None);
        using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
            (await update.Handle(new UpdateRecordRequest(poId, V((_r.Discount, 0)), row!.RowVersion, null,
                new HashSet<string> { $"f_{_r.Discount}" }), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        return await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Locked_UnlockTransitionEventClearsLockedField_Succeeds()
    {
        var po = await ApprovedPo();
        await SetRaw(po.Id, _r.Discount, 100);

        (await UnlockThenSystemWrite(po.Id)).Should().BeNull();
        StateOf(_r, po.Id).Should().Be(_r.Draft);
        Num(po.Id, _r.Discount).Should().Be(0);
    }

    [Fact]
    public async Task Locked_UnlockTransition_UserAlsoChangedLockedField_Returns400()
    {
        // 使用者（或整合）直接送來的值同次改鎖定欄位，即使後面的事件把它改回去，輸入界線之前的變動仍要擋
        var po = await ApprovedPo();
        await SetRaw(po.Id, _r.Discount, 100);

        var res = await UnlockThenSystemWrite(po.Id, userAlsoChangesDiscount: true);

        res.Should().NotBeNull();
        res!.Errors.Should().ContainKey($"f_{_r.Discount}");
    }

    [Fact]
    public async Task Locked_EventUpdatesLockedField_RollsBack400()
    {
        var po = await ApprovedPo();
        var created = await Send(new CreateEventRequest(Event("po-lock-touch", _r.Extra, new EventTrigger("create", null, null, null),
            UpdateVia("d", _r.Module, _r.ExtraRef, Const(_r.Discount, "7")))));
        created.IsCreated.Should().BeTrue(Dump(created));

        var res = await NewExtra(po.Id, 100);

        ShouldRejectField(res, _r.Discount);
        Num(po.Id, _r.Discount).Should().Be(0);
        Db.Context.Set<Record>().AsNoTracking().Count(x => x.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == _r.Extra).Id).Should().Be(0);
    }

    /// <summary>在一個 scope 內直接改記錄的計算欄位並跑後盾（silent 時先標靜默）。</summary>
    private async Task<ServiceResult?> WriteComputedDirect(long poId, object value, bool silent)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var row = (await sp.GetRequiredService<IRecordRepository>().GetAsync(poId, CancellationToken.None))!;
        var values = RecordValueProcessor.Parse(row.ValuesJson);
        values[$"f_{_r.NetTotal}"] = JsonSerializer.SerializeToElement(value);
        row.ValuesJson = RecordValueProcessor.Serialize(values);
        if (silent) sp.GetRequiredService<IWriteJournal>().MarkSilent(row);
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        return await sp.GetRequiredService<ApprovalBackstopStage>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Backstop_NonSilentComputedChangeOnLocked_Returns400_SilentAllowed()
    {
        var po = await ApprovedPo();

        var loud = await WriteComputedDirect(po.Id, 999, silent: false);
        loud.Should().NotBeNull();
        loud!.Errors.Should().ContainKey($"f_{_r.NetTotal}");

        (await WriteComputedDirect(po.Id, 888, silent: true)).Should().BeNull();
    }

    private DetailRowInput NewLine() => new(null, V((TitleId(_r.Line), "列"), (_r.LineQty, 5), (_r.LinePrice, 10)), null);

    [Fact]
    public async Task Locked_AddDetailLine_WhenDetailRollupLocked_Returns400()
    {
        var po = await ApprovedPo();

        var res = await Put(po.Id, [], new() { [_r.Line] = [NewLine()] });

        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey($"details.{_r.Line}");
        Db.Context.Set<Record>().AsNoTracking().Count(x => x.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == _r.Line).Id).Should().Be(1);
    }

    [Fact]
    public async Task Locked_AddDetailLine_WhenConditionOnlySupplier_Succeeds()
    {
        var po = await ApprovedPo(r => Task.FromResult(r.Supplier));

        (await Put(po.Id, [], new() { [_r.Line] = [NewLine()] })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Locked_PutHeaderWithUnchangedDetails_Succeeds()
    {
        var po = await ApprovedPo();
        var line = LineOf(po.Id);

        var res = await Put(po.Id, V((_r.Memo, "x")), new() { [_r.Line] = [new DetailRowInput(line.Id, new Dictionary<string, JsonElement?>(), line.RowVersion)] });

        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Locked_SetNullOnLockedReference_RollsBack400()
    {
        long backup = 0, vendorId = 0;
        // 流程只引用「備用供應商」（參照設 SetNull），所以它是鎖定欄位；單據在核准前就帶了這個供應商
        var po = await ApprovedPo(
            async r =>
            {
                backup = (await MustAdd(r.Module, FieldType.Reference, RefTo(r.Vendor, onDelete: ReferenceOnDelete.SetNull), "備用供應商")).Id;
                await GrantEveryone(r.Vendor);
                var title = (await Detail(r.Vendor)).TitleFieldId!.Value;
                vendorId = ((DynamicRecordResponse)(await Send(new CreateRecordRequest(r.Vendor, V((title, "v1"))))).Data!).Id;
                return backup;
            },
            _ => V((backup, vendorId)));

        var res = await Send(new DeleteRecordRequest(vendorId, Rec(vendorId).Row.RowVersion));

        res.IsSuccess.Should().BeFalse();
        Rec(vendorId).Row.DeleteFlag.Should().BeFalse();
        Rec(po.Id).Values.Should().ContainKey($"f_{backup}");
    }

    [Fact]
    public async Task Locked_RollupFromOtherModule_Recalculated_Allowed()
    {
        var po = await ApprovedPo();

        var res = await NewExtra(po.Id, 250);

        res.IsCreated.Should().BeTrue(Dump(res));
        Num(po.Id, _r.ExtraSum).Should().Be(250);
    }

    [Fact]
    public async Task Locked_ApprovalEditableFieldOnOwnInstance_StillExempt()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_r.Discount, 0)));
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);

        var res = await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, V((_r.DueDate, "2026-12-01"))));

        res.IsSuccess.Should().BeTrue(Dump(res));
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
        Rec(po.Id).Values[$"f_{_r.DueDate}"]!.Value.GetString().Should().Be("2026-12-01");
    }

    [Fact]
    public async Task PendingFromLocked_PutLockedField_Returns409()
    {
        var po = await ApprovedPo();
        As("buyer1");
        (await MustSubmit(po.Id, _r.Cancelled)).InstanceId.Should().NotBeNull();
        As("admin");

        var res = await Put(po.Id, V((_r.Discount, 50)));

        res.IsConflict.Should().BeTrue();
    }
}
