using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>設計器遇到簽核中的實例（設計 §8.6）：不能改寫記錄值的定義，也不能拆掉實例要走的轉移與狀態。</summary>
public class PendingApprovalGuardTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;

    /// <summary>草稿 → 已確認 需簽核；已確認 → 草稿、部分進貨 是一般轉移；rejectTo 不為 null 時流程退回到該狀態（草稿 → 該狀態另加一般轉移）。</summary>
    private async Task Rig(long? rejectTo = null)
    {
        _r = await BuildPurchase();
        await SeedFlow(_r, "po-a", _r.Confirmed, 1,
            Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))) with { RejectTo = rejectTo });
        (await SaveWorkflow(_r, Transitions(rejectTo))).IsSuccess.Should().BeTrue();
        await GrantEveryone(_r.Module, _r.Extra);
    }

    private List<WorkflowTransition> Transitions(long? rejectTo = null)
    {
        var list = new List<WorkflowTransition> { Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial) };
        if (rejectTo is { } rt) list.Add(Plain(_r.Draft, rt));
        return list;
    }

    /// <summary>建單並送簽，單據留在簽核中。</summary>
    private async Task<long> Pend(string title = "單據", Dictionary<string, System.Text.Json.JsonElement?>? extra = null)
    {
        As("buyer1");
        var values = V((_r.Discount, 100));
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var po = await NewPo(_r, title: title, extra: values);
        await MustSubmit(po.Id, _r.Confirmed);
        As("admin");
        return po.Id;
    }

    private long BlockOf(long fieldId) => Db.Context.Set<Field>().AsNoTracking().Single(f => f.Id == fieldId).BlockId;

    private Task<ServiceResult> SetFormula(long fieldId, string formula, int scale = 0) =>
        Send(new UpdateFieldRequest(fieldId, FieldType.Formula, Text("公式"),
            new FieldConfig { Formula = formula, ResultType = "number", Scale = scale }, BlockOf(fieldId)));

    private static void Blocked(ServiceResult res, params string[] contains)
    {
        res.IsConflict.Should().BeTrue(res.Message + string.Join(";", res.Errors.Select(e => e.Key + e.Value[0])));
        res.Message.Should().Contain("正在簽核中", System.Text.Json.JsonSerializer.Serialize(res.Data));
        foreach (var c in contains) res.Message.Should().Contain(c);
    }

    // ───────── Task 11：改寫值的定義 ─────────

    [Fact]
    public async Task UpdateFormula_WithPending_Returns409ListingRecord()
    {
        await Rig();
        await Pend("PO-甲");
        Blocked(await SetFormula(_r.NetTotal, $"{{{_r.LineSum}}}"), "PO-甲");
    }

    [Fact]
    public async Task UpdateFormula_SixPending_ListsFiveAndCount()
    {
        await Rig();
        for (var i = 1; i <= 6; i++) await Pend($"PO-{i}");
        var res = await SetFormula(_r.NetTotal, $"{{{_r.LineSum}}}");
        Blocked(res, "PO-1", "PO-5", "等 6 筆");
        res.Message.Should().NotContain("PO-6");
    }

    [Fact]
    public async Task AddFormulaField_WithPending_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await Send(new AddFieldRequest(_r.Module, BlockOf(_r.Memo), FieldType.Formula, Text("新公式"),
            new FieldConfig { Formula = $"{{{_r.Discount}}} + 1", ResultType = "number", Scale = 0 })));
    }

    [Fact]
    public async Task UpdateFill_WithPending_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await Send(new UpdateFieldRequest(_r.Supplier, FieldType.Reference, Text("供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Default, [new FillMapping(_r.VendorName, _r.SupplierName)]) }, BlockOf(_r.Supplier))));
    }

    [Fact]
    public async Task UpdateScale_FormulaWithPending_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await SetFormula(_r.Total, $"{{{_r.LineSum}}} + {{{_r.ExtraSum}}}", scale: 1));
    }

    [Fact]
    public async Task UpdateScale_NumberFieldWithPending_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await Send(new UpdateFieldRequest(_r.Discount, FieldType.Decimal, Text("折扣"), new FieldConfig { Scale = 1 }, BlockOf(_r.Discount))));
    }

    /// <summary>已軟刪除的單據在 <paramref name="fieldId"/> 留著值：刪除欄位時要清它們，Purge.Records 才大於 0。</summary>
    private async Task SoftDeletedPoWithValue(long fieldId)
    {
        var gone = await NewPo(_r, extra: V((fieldId, "x")));
        (await Send(new DeleteRecordRequest(gone.Id, Rec(gone.Id).Row.RowVersion))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFieldWithValues_WithPending_Returns409()
    {
        await Rig();
        await SoftDeletedPoWithValue(_r.Memo);
        await Pend();
        (await Deactivate(_r.Memo)).IsSuccess.Should().BeTrue();   // 刪除欄位要先停用
        Blocked(await Send(new DeleteFieldRequest(_r.Memo, 1)));
    }

    [Fact]
    public async Task ReactivateFormulaField_WithPending_Returns409()
    {
        await Rig();
        var derived = await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_r.Discount}}} + 1", ResultType = "number", Scale = 0 }, "折扣加一");
        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
        await Pend();
        Blocked(await Send(new SetFieldStatusRequest(derived.Id, FieldStatus.Active)));
    }

    [Fact]
    public async Task DeactivateFormulaField_WithPending_Succeeds()
    {
        await Rig();
        var derived = await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_r.Discount}}} + 1", ResultType = "number", Scale = 0 }, "折扣加一");
        await Pend();
        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteEmptyField_WithPending_Succeeds()
    {
        await Rig();
        await Pend();
        var tmp = await MustAdd(_r.Module, FieldType.Text, label: "暫用");
        (await Deactivate(tmp.Id)).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteFieldRequest(tmp.Id, 0));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DeleteBlockWithValues_WithPending_Returns409()
    {
        await Rig();
        var block = (BlockResponse)(await Send(new AddBlockRequest(_r.Module, Text("新區塊")))).Data!;
        var field = (FieldResponse)(await Send(new AddFieldRequest(_r.Module, block.Id, FieldType.Text, Text("區塊欄位"), new FieldConfig()))).Data!;
        await SoftDeletedPoWithValue(field.Id);
        await Pend();
        (await Deactivate(field.Id)).IsSuccess.Should().BeTrue();
        Blocked(await Send(new DeleteBlockRequest(block.Id, 1)));
    }

    [Fact]
    public async Task RenameOptionUsedInTitle_WithPending_Returns409()
    {
        await Rig();
        var set = await Send(new UpdateModuleRequest(_r.Module, Text("採購單"), null, null, TitleId(_r.Module), $"{{{TitleId(_r.Module)}}} {{{_r.Status}}}"));
        set.IsSuccess.Should().BeTrue(Dump(set));
        await Pend("PO-甲");
        Blocked(await Send(new UpdateOptionRequest(_r.Cancelled, Text("作廢"), null, true)), "PO-甲");
    }

    [Fact]
    public async Task ChangeTitleField_WithPending_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await Send(new UpdateModuleRequest(_r.Module, Text("採購單"), null, null, _r.Memo)));
    }

    [Fact]
    public async Task UpdateDetailFormula_HeaderPending_Returns409()
    {
        await Rig();
        await Pend("PO-甲");
        Blocked(await SetFormula(_r.LineSubtotal, $"{{{_r.LineQty}}} * {{{_r.LinePrice}}} + 1"), "PO-甲");
    }

    [Fact]
    public async Task UpdateDetailFormula_DetailWithoutRollup_HeaderPending_Returns409()
    {
        await Rig();
        // 另一個明細模組，表頭沒有彙總它：只能靠「本模組是明細 → 看表頭」這條範圍規則擋
        await NewModule("po-note");
        await MustAdd("po-note", FieldType.Reference, RefTo(_r.Module, ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd("po-note", FieldType.Decimal, new FieldConfig { Scale = 0 }, "數量");
        var calc = await MustAdd("po-note", FieldType.Formula,
            new FieldConfig { Formula = $"{{{qty.Id}}} * 2", ResultType = "number", Scale = 0 }, "加倍");
        (await Send(new ActivateModuleRequest("po-note"))).IsSuccess.Should().BeTrue();
        await Pend("PO-甲");
        Blocked(await SetFormula(calc.Id, $"{{{qty.Id}}} * 3"), "PO-甲");
    }

    [Fact]
    public async Task UpdateExtraModuleFormula_ParentPending_Returns409()
    {
        await Rig();
        var extra = await MustAdd(_r.Extra, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_r.ExtraAmount}}} * 1", ResultType = "number", Scale = 0 }, "加成");
        await Pend("PO-甲");
        Blocked(await SetFormula(extra.Id, $"{{{_r.ExtraAmount}}} * 2"), "PO-甲");
    }

    [Fact]
    public async Task UpdateLabel_WithPending_Succeeds()
    {
        await Rig();
        await Pend();
        var res = await Send(new UpdateFieldRequest(_r.Memo, FieldType.Text, Text("新備註"), new FieldConfig(), BlockOf(_r.Memo)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task AddPlainField_WithPending_Succeeds()
    {
        await Rig();
        await Pend();
        var res = await AddField(_r.Module, FieldType.Text, label: "新欄位");
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task UpdateFormula_AfterApproved_NoPending_Succeeds()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, extra: V((_r.Discount, 100)));
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        As("admin");

        var res = await SetFormula(_r.NetTotal, $"{{{_r.LineSum}}}");
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    // ───────── Task 12：轉移與狀態 ─────────

    [Fact]
    public async Task SaveWorkflow_RemovePendingTransition_Returns409()
    {
        await Rig();
        await Pend("PO-甲");
        Blocked(await SaveWorkflow(_r, [Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial)]), "PO-甲");
    }

    [Fact]
    public async Task SaveWorkflow_TurnOffApprovalOnPendingTransition_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await SaveWorkflow(_r, [Plain(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial)]));
    }

    [Fact]
    public async Task SaveWorkflow_RemoveRejectToTransition_Returns409()
    {
        await Rig(rejectTo: null);
        // 另建綁定「退回到已退回」版本的流程與實例
        var other = await BuildPurchase("pr");
        var flow = await SeedFlow(other, "pr-a", other.Confirmed, 1,
            Def(null, Stage("lead", Group(PurchaseLeadGroup))) with { RejectTo = other.Returned });
        List<WorkflowTransition> Wf(bool withReject) =>
            [Needs(other.Draft, other.Confirmed), Plain(other.Confirmed, other.Draft), .. withReject ? [Plain(other.Draft, other.Returned)] : Array.Empty<WorkflowTransition>()];
        (await SaveWorkflow(other, Wf(true))).IsSuccess.Should().BeTrue();
        await SeedInstance(other, flow, recordId: 1);

        Blocked(await SaveWorkflow(other, Wf(false)));
    }

    [Fact]
    public async Task SaveWorkflow_RemovePendingStateOption_Returns409()
    {
        await Rig();
        await Pend();
        Blocked(await SaveWorkflow(_r, [Plain(_r.Draft, _r.Partial)], states: AllStates(_r).Where(s => s.OptionId != _r.Confirmed).ToList()));
    }

    [Fact]
    public async Task SaveWorkflow_ChangeStatusField_Returns409()
    {
        await Rig();
        await Pend();
        // 換成另一個狀態欄位：輸入本身合法（選項屬於新欄位），只有實例綁定的狀態欄位變了
        var other = await MustAdd(_r.Module, FieldType.Enum, label: "第二狀態");
        var x = ((OptionResponse)(await Send(new AddOptionRequest(other.Id, Text("甲"), null))).Data!).Id;
        var y = ((OptionResponse)(await Send(new AddOptionRequest(other.Id, Text("乙"), null))).Data!).Id;
        var input = new WorkflowInput(other.Id, x, [new WorkflowState(x), new WorkflowState(y)], [Plain(x, y)]);
        Blocked(await Send(new SaveWorkflowRequest(_r.Module, input)));
    }

    [Fact]
    public async Task SaveWorkflow_UnrelatedTransitionAdded_Succeeds()
    {
        await Rig();
        await Pend();
        var res = await SaveWorkflow(_r, [.. Transitions(), Plain(_r.Partial, _r.Received)]);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    // 存狀態機的輸入驗證（400）排在守衛前面，合法輸入裡「狀態被拿掉但轉移還在」與「狀態欄位換了但狀態沒換」到不了守衛，
    // 所以這兩條規則直接對守衛下候選狀態機驗證。
    private async Task<ServiceResult?> GuardCandidate(Func<Workflow, Workflow> change)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, true, CancellationToken.None))!;
        return await sp.GetRequiredService<PendingApprovalGuard>().RejectWorkflowChangeAsync(module, change(module.Workflow!), CancellationToken.None);
    }

    [Fact]
    public async Task Guard_CandidateDropsPendingState_TransitionKept_Returns409()
    {
        await Rig();
        await Pend();
        var res = await GuardCandidate(w => new Workflow
        {
            ModuleId = w.ModuleId, StatusFieldId = w.StatusFieldId, InitialOptionId = w.InitialOptionId,
            States = w.States.Where(s => s.OptionId != _r.Confirmed).ToList(), Transitions = w.Transitions
        });
        res.Should().NotBeNull();
        res!.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_CandidateChangesStatusField_StatesKept_Returns409()
    {
        await Rig();
        await Pend();
        var res = await GuardCandidate(w => new Workflow
        {
            ModuleId = w.ModuleId, StatusFieldId = w.StatusFieldId + 1000, InitialOptionId = w.InitialOptionId,
            States = w.States, Transitions = w.Transitions
        });
        res.Should().NotBeNull();
        res!.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_UnchangedCandidate_ReturnsNull()
    {
        await Rig();
        await Pend();
        (await GuardCandidate(w => new Workflow
        {
            ModuleId = w.ModuleId, StatusFieldId = w.StatusFieldId, InitialOptionId = w.InitialOptionId,
            States = w.States, Transitions = w.Transitions
        })).Should().BeNull();
    }

    [Fact]
    public async Task DeleteWorkflow_WithPending_Returns409()
    {
        await Rig();
        await Pend("PO-甲");
        Blocked(await Send(new DeleteWorkflowRequest(_r.Module)), "PO-甲");
    }

    [Fact]
    public async Task DeleteWorkflow_NoPending_Succeeds()
    {
        var other = await BuildPurchase("pl");
        var res = await Send(new DeleteWorkflowRequest(other.Module));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DeactivateOption_UsedByPending_Returns409()
    {
        await Rig();
        await Pend("PO-甲");
        Blocked(await Send(new UpdateOptionRequest(_r.Confirmed, Text("已確認"), null, false)), "PO-甲");
    }

    [Fact]
    public async Task DeactivateOption_Unused_Succeeds()
    {
        await Rig();
        await Pend();
        var res = await Send(new UpdateOptionRequest(_r.Cancelled, Text("已取消"), null, false));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }
}
