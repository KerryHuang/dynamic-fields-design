using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>設計器遇到停在簽核鎖定狀態的記錄（設計 §8.6）：不能整批解鎖，也不能清掉鎖定欄位的值。</summary>
public class LockedDataGuardTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _files;

    /// <summary>草稿 → 已確認 需簽核；已確認 → 部分進貨 → 已進貨、已確認 → 草稿是一般轉移；單據有一個附件欄位。流程條件引用淨額，所以明細一起鎖。</summary>
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _files = (await MustAdd(_r.Module, FieldType.Attachment, label: "附件")).Id;
        await SeedFlow(_r, "po-a", _r.Confirmed, 1, Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(_r, Base())).IsSuccess.Should().BeTrue();
        await GrantEveryone(_r.Module, _r.Extra);
    }

    private List<WorkflowTransition> Base() =>
        [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received)];

    /// <summary>建單、送簽、核准，單據停在已確認（鎖定），沒有進行中實例。</summary>
    private async Task<long> Approved(string title = "單據", bool withFile = false)
    {
        As("buyer1");
        var po = await NewPo(_r, title: title, extra: V((_r.Discount, 100), (_r.Memo, "備註")));
        if (withFile)
            (await Send(new UploadAttachmentRequest(po.Id, _files, new MemoryStream(Encoding.UTF8.GetBytes("x")), "a.txt", 1))).IsCreated.Should().BeTrue();
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        As("admin");
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
        return po.Id;
    }

    private async Task MoveTo(long id, long state) =>
        (await Put(id, V((_r.Status, $"opt_{state}")))).IsSuccess.Should().BeTrue();

    private static void Blocked(ServiceResult res, params string[] contains)
    {
        res.IsConflict.Should().BeTrue(Dump(res));
        foreach (var c in contains) res.Message.Should().Contain(c);
    }

    // ───────── 整批解鎖（裁決 8） ─────────

    [Fact]
    public async Task SaveWorkflow_TurnOffApproval_RecordsInLockedState_Returns409WithCount()
    {
        await Rig();
        await Approved("甲");
        await Approved("乙");
        Blocked(await SaveWorkflow(_r, [Plain(_r.Draft, _r.Confirmed), .. Base().Skip(1)]), "2 筆", "解除鎖定");
    }

    /// <summary>已確認走一般轉移延伸到的「部分進貨」也鎖定；標成可編輯就解鎖了。</summary>
    [Fact]
    public async Task SaveWorkflow_MarkExtendedLockedStateEditable_RecordsThere_Returns409()
    {
        await Rig();
        var po = await Approved();
        await MoveTo(po, _r.Partial);
        Blocked(await SaveWorkflow(_r, Base(), AllStates(_r, _r.Partial)), "1 筆");
    }

    [Fact]
    public async Task SaveWorkflow_MarkExtendedLockedStateEditable_NoRecordsThere_Succeeds()
    {
        await Rig();
        await Approved();   // 停在已確認，沒有記錄在部分進貨
        (await SaveWorkflow(_r, Base(), AllStates(_r, _r.Partial))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveWorkflow_ChangeStatusField_RecordsLocked_Returns409()
    {
        await Rig();
        await Approved();
        var other = await MustAdd(_r.Module, FieldType.Enum, label: "狀態二");
        var a = ((OptionResponse)(await Send(new AddOptionRequest(other.Id, Text("甲"), null))).Data!).Id;
        var b = ((OptionResponse)(await Send(new AddOptionRequest(other.Id, Text("乙"), null))).Data!).Id;
        var res = await Send(new SaveWorkflowRequest(_r.Module,
            new WorkflowInput(other.Id, a, [new(a), new(b)], [Plain(a, b)])));
        Blocked(res, "1 筆");
    }

    [Fact]
    public async Task SaveWorkflow_TurnOffApproval_NoRecordsInLockedState_Succeeds()
    {
        await Rig();
        var po = await Approved();
        await MoveTo(po, _r.Draft);   // 轉回可編輯的狀態
        (await SaveWorkflow(_r, [Plain(_r.Draft, _r.Confirmed), .. Base().Skip(1)])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveWorkflow_TurnOffApproval_DeletedRecordInLockedState_Succeeds()
    {
        await Rig();
        var po = await Approved();
        var row = await Db.Context.Set<Record>().SingleAsync(x => x.Id == po);
        row.DeleteFlag = true;   // 已刪除的記錄不算
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await SaveWorkflow(_r, [Plain(_r.Draft, _r.Confirmed), .. Base().Skip(1)])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveWorkflow_AddApprovalTransition_Succeeds()
    {
        await Rig();
        await Approved();
        await SeedFlow(_r, "po-c", _r.Cancelled, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var res = await SaveWorkflow(_r, [.. Base(), Needs(_r.Confirmed, _r.Cancelled)]);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task SaveWorkflow_RecordsOnlyInDraft_Succeeds()
    {
        await Rig();
        As("buyer1");
        await NewPo(_r);
        As("admin");
        (await SaveWorkflow(_r, [Plain(_r.Draft, _r.Confirmed), .. Base().Skip(1)])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteWorkflow_RecordsInLockedState_Returns409()
    {
        await Rig();
        await Approved();
        Blocked(await Send(new DeleteWorkflowRequest(_r.Module)), "1 筆");
    }

    [Fact]
    public async Task DeleteWorkflow_NoRecordsInLockedState_Succeeds()
    {
        await Rig();
        (await Send(new DeleteWorkflowRequest(_r.Module))).IsSuccess.Should().BeTrue();
    }

    /// <summary>鎖定集合只看狀態與轉移，不看選項啟用與否：停用鎖定狀態的選項不會改變鎖定集合，所以這一處沒有加守衛。</summary>
    [Fact]
    public async Task DeactivateOption_LockedState_LockedSetUnchanged()
    {
        await Rig();
        await Approved();
        var wf = Db.Context.Set<Workflow>().AsNoTracking().Single(w => w.ModuleId == _r.ModuleId);
        ApprovalTopology.LockedStates(wf).Should().Contain(_r.Confirmed);
        var res = await Send(new UpdateOptionRequest(_r.Received, Text("已進貨"), null, false));   // 鎖定集合以外的選項，作為對照
        res.IsSuccess.Should().BeTrue(Dump(res));
        ApprovalTopology.LockedStates(wf).Should().Contain(_r.Confirmed);
    }

    // ───────── 刪除鎖定欄位的值（裁決 10） ─────────

    private async Task<ServiceResult?> GuardPurge(string module, params long[] fieldIds)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var m = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(module, true, CancellationToken.None))!;
        var fields = m.Blocks.SelectMany(b => b.Fields).Where(f => fieldIds.Contains(f.Id)).ToList();
        return await sp.GetRequiredService<LockedDataGuard>().RejectPurgeAsync(m, fields, CancellationToken.None);
    }

    /// <summary>有值的欄位本來就被安全刪除的「欄位有資料」擋下（409）；這條證明端到端仍是 409。</summary>
    [Fact]
    public async Task DeleteAttachmentField_LockedRecordHasFile_Returns409()
    {
        await Rig();
        await Approved(withFile: true);
        (await Deactivate(_files)).IsSuccess.Should().BeTrue();
        (await Send(new DeleteFieldRequest(_files, 0))).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteLockedField_LockedRecordsEmpty_Succeeds()
    {
        await Rig();
        await Approved();   // 沒有附件
        (await Deactivate(_files)).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteFieldRequest(_files, 0));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DeleteUnlockedField_WithLockedRecords_Succeeds()
    {
        await Rig();
        await Approved(withFile: true);
        (await Deactivate(_r.DueDate)).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteFieldRequest(_r.DueDate, 0));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DeleteBlock_ContainsLockedFieldWithValue_Returns409()
    {
        await Rig();
        await Approved(withFile: true);
        var block = Db.Context.Set<Field>().AsNoTracking().Single(f => f.Id == _files).BlockId;
        (await Send(new DeleteBlockRequest(block, 0))).IsConflict.Should().BeTrue();
    }

    // 安全刪除對「有值的欄位」已先擋，守衛本身的條件直接對守衛驗證。

    [Fact]
    public async Task Guard_PurgeLockedAttachment_LockedRecordHasFile_Returns409()
    {
        await Rig();
        await Approved(withFile: true);
        var res = await GuardPurge(_r.Module, _files);
        res.Should().NotBeNull();
        Blocked(res!, "1 筆", "附件");
    }

    [Fact]
    public async Task Guard_PurgeLockedAttachment_RecordNotLocked_ReturnsNull()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r);   // 草稿，可編輯
        (await Send(new UploadAttachmentRequest(po.Id, _files, new MemoryStream([1]), "a.txt", 1))).IsCreated.Should().BeTrue();
        As("admin");
        (await GuardPurge(_r.Module, _files)).Should().BeNull();
    }

    [Fact]
    public async Task Guard_PurgeLockedAttachment_EmptyArray_ReturnsNull()
    {
        await Rig();
        var po = await Approved(withFile: true);
        var row = await Db.Context.Set<Record>().SingleAsync(x => x.Id == po);
        var node = System.Text.Json.Nodes.JsonNode.Parse(row.ValuesJson)!.AsObject();
        node[$"f_{_files}"] = new System.Text.Json.Nodes.JsonArray();   // 檔案都被移除，剩空陣列
        row.ValuesJson = node.ToJsonString();
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await GuardPurge(_r.Module, _files)).Should().BeNull();
    }

    [Fact]
    public async Task Guard_PurgeUnlockedField_LockedRecordHasValue_ReturnsNull()
    {
        await Rig();
        await Approved();
        (await GuardPurge(_r.Module, _r.Memo)).Should().BeNull();
    }

    /// <summary>流程條件引用淨額，其上游含明細小計彙總，所以表頭鎖定時明細列一起鎖。</summary>
    [Fact]
    public async Task Guard_PurgeDetailField_HeaderLocked_DetailRowsLocked_Returns409()
    {
        await Rig();
        await Approved();
        As("buyer1");
        await NewPo(_r);   // 另一張還在草稿的單據，它的明細列不算
        As("admin");
        var res = await GuardPurge(_r.Line, _r.LineQty);
        res.Should().NotBeNull();
        Blocked(res!, "1 筆", "數量");
    }

    [Fact]
    public async Task Guard_PurgeDetailField_HeaderNotLocked_ReturnsNull()
    {
        await Rig();
        As("buyer1");
        await NewPo(_r);   // 草稿
        As("admin");
        (await GuardPurge(_r.Line, _r.LineQty)).Should().BeNull();
    }

    private async Task<ServiceResult?> GuardUnlock(Func<Workflow, Workflow?> change)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var m = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, true, CancellationToken.None))!;
        return await sp.GetRequiredService<LockedDataGuard>().RejectUnlockAsync(m, change(m.Workflow!), CancellationToken.None);
    }

    /// <summary>狀態被整個拿掉（鎖定狀態不在候選的狀態清單）也算解鎖。</summary>
    [Fact]
    public async Task Guard_CandidateDropsLockedState_RecordsThere_Returns409()
    {
        await Rig();
        await Approved();
        var res = await GuardUnlock(w => new Workflow
        {
            ModuleId = w.ModuleId, StatusFieldId = w.StatusFieldId, InitialOptionId = w.InitialOptionId,
            States = w.States.Where(s => s.OptionId != _r.Confirmed).ToList(),
            Transitions = w.Transitions.Where(t => t.From != _r.Confirmed && t.To != _r.Confirmed).ToList()
        });
        res.Should().NotBeNull();
        Blocked(res!, "1 筆");
    }

    [Fact]
    public async Task Guard_CandidateChangesStatusField_SameStatesKept_Returns409()
    {
        await Rig();
        await Approved();
        var res = await GuardUnlock(w => new Workflow
        {
            ModuleId = w.ModuleId, StatusFieldId = w.StatusFieldId + 1000, InitialOptionId = w.InitialOptionId,
            States = w.States, Transitions = w.Transitions
        });
        res.Should().NotBeNull();
        Blocked(res!, "1 筆");
    }

    [Fact]
    public async Task Guard_CandidateSameAsCurrent_ReturnsNull()
    {
        await Rig();
        await Approved();
        (await GuardUnlock(w => w)).Should().BeNull();
    }
}
