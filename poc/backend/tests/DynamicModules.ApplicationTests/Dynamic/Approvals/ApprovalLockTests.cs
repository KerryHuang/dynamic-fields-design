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
                Stage("sup", Group(PurchaseLeadGroup), Cond(await onlyCondition(_r), "eq", 1)));
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
}
