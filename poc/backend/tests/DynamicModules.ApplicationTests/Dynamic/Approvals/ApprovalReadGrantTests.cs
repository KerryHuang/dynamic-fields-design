using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核人的臨時讀取權（設計 §10.1；劇本二第 3、5 步，§17.5 第一輪 7）。</summary>
public class ApprovalReadGrantTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _amount, _files;

    /// <summary>
    /// 單據多一個金額欄位（Currency）與附件欄位；只有 buyers 群組（buyer1、buyer2）有模組的讀寫與 Pricing；
    /// finance 群組（fin1、fin2）對模組沒有任何權限。流程一關 finance 或簽。
    /// </summary>
    private async Task Rig(bool editableAmount = false)
    {
        _r = await BuildPurchase();
        _amount = (await MustAdd(_r.Module, FieldType.Currency, label: "金額")).Id;
        _files = (await MustAdd(_r.Module, FieldType.Attachment, label: "附件")).Id;
        await SeedFlow(_r, "po-g", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup), editable: editableAmount ? [_amount] : null)));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled)])).IsSuccess.Should().BeTrue();
        await Permissions();
    }

    private async Task Permissions(params FieldPermissionDto[] fields)
    {
        var group = Db.Context.Set<PermissionGroup>().AsNoTracking().FirstOrDefault(g => g.Key == "buyers");
        if (group is null)
        {
            group = new PermissionGroup { Key = "buyers", NameI18n = Text("buyers") };
            Db.Context.Add(group);
            await Db.Context.SaveChangesAsync();
            Db.Context.AddRange(new UserGroupMapping { GroupId = group.Id, UserId = "buyer1" }, new UserGroupMapping { GroupId = group.Id, UserId = "buyer2" });
            await Db.Context.SaveChangesAsync();
            Db.Context.ChangeTracker.Clear();
        }
        As("admin");
        var res = await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(group.Id, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All)], [], [.. fields], [])));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<(long RecordId, long InstanceId)> Pending()
    {
        As("buyer1");
        var po = await NewPo(_r, price: 30000, extra: V((_amount, 30000)));
        return (po.Id, (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value);
    }

    private Task<ServiceResult> Get(string user, long id)
    {
        As(user);
        return Send(new GetRecordRequest(id));
    }

    [Fact]
    public async Task OpenTaskActor_NoModuleRead_GetsRecordWithPricing()
    {
        await Rig();
        var (po, _) = await Pending();
        var res = await Get("fin1", po);
        res.IsSuccess.Should().BeTrue(Dump(res));
        var body = (DynamicRecordResponse)res.Data!;
        body.Values[$"f_{_amount}"]!.Value.GetDecimal().Should().Be(30000);
        body.Details![_r.Line].Should().ContainSingle();
        (await Get("emp1", po)).IsNotFound.Should().BeTrue();   // 沒有任務、沒有模組權限：與不存在相同的 404（a3d 審查修正）
    }

    [Fact]
    public async Task OtherRecord_StillDenied()
    {
        await Rig();
        await Pending();
        As("buyer1");
        var other = await NewPo(_r, price: 100);
        var res = await Get("fin1", other.Id);
        res.IsNotFound.Should().BeTrue(Dump(res));   // 沒有模組 Read、也沒參與這筆簽核：與不存在相同的 404（a3d 審查修正）
    }

    [Fact]
    public async Task List_DoesNotIncludeIt()
    {
        await Rig();
        await Pending();
        As("fin1");
        var res = await Send(new GetPagedRecordsRequest(_r.Module, 1, 20, null, [], [], null));
        res.IsSuccess.Should().BeFalse();
        res.IsForbidden.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Export_403()
    {
        await Rig();
        await Pending();
        As("fin1");
        var res = await Send(new ExportRecordsRequest(_r.Module, "csv", null, [], [], null, "zh-TW"));
        res.IsForbidden.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task AfterTaskClosed_Denied()
    {
        await Rig();
        var (po, id) = await Pending();
        (await Get("fin2", po)).IsSuccess.Should().BeTrue();
        As("fin1");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "fin1");
        (await Send(new RejectTaskRequest(task.Id, Inst(id).RowVersion, "請補資料", RejectTaskHandler.ToInitiator))).IsSuccess.Should().BeTrue();
        (await Get("fin1", po)).IsForbidden.Should().BeTrue();   // 退回的人參與過：沒有 Read 維持 403
        (await Get("fin2", po)).IsNotFound.Should().BeTrue();   // 同關被關閉的任務一起收回；沒動作過，不是參與者（a3d 審查修正）
    }

    [Fact]
    public async Task DelegatedOriginal_AlsoGranted_TransferredOriginalNot()
    {
        // 可動作者：負責人，或 Reason = delegated 時的原簽核人；轉交的原簽核人不算（設計 §10.1）
        await Rig();
        var (po, id) = await Pending();
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "fin1")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeUserId, "emp2").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        (await Get("fin1", po)).IsSuccess.Should().BeTrue();
        (await Get("emp2", po)).IsSuccess.Should().BeTrue();
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "emp2")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Reason, ApprovalTaskReason.Transferred));
        (await Get("fin1", po)).IsNotFound.Should().BeTrue();   // 不是可動作者、也沒動作過：不是參與者（a3d 審查修正）
        (await Get("emp2", po)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ProgramInactive_NotGranted()
    {
        // 比照 guard：program 停用期間，可動作者也不能讀單筆
        await Rig();
        var (po, _) = await Pending();
        var code = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Module).ProgramCode;
        (await Db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == code)).Active = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await Get("fin1", po)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ReadButOutOfScope_OpenActorGranted_OtherRecord404()
    {
        // 有 Read 但資料範圍只限自己的記錄：可動作者對簽核中的這一筆照樣讀得到；別筆維持 404
        await Rig();
        var buyers = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "buyers").Id;
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
        [
            new GroupPermissionDto(buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All),
            new GroupPermissionDto(FinanceGroup, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.Own)
        ], [], [], [])))).IsSuccess.Should().BeTrue();
        var (po, _) = await Pending();
        As("buyer1");
        var other = await NewPo(_r, price: 100);

        (await Get("fin1", po)).IsSuccess.Should().BeTrue();
        (await Get("fin1", other.Id)).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AttachmentFieldHiddenForApprover_Download404()
    {
        await Rig();
        await Permissions(new FieldPermissionDto(_files, FinanceGroup, FieldAccess.Hidden));
        As("buyer1");
        var po = await NewPo(_r, price: 30000);
        var up = await Send(new UploadAttachmentRequest(po.Id, _files, new MemoryStream(Encoding.UTF8.GetBytes("收據")), "receipt.txt", 6));
        up.IsCreated.Should().BeTrue(Dump(up));
        var file = (UploadedAttachment)up.Data!;
        await MustSubmit(po.Id, _r.Confirmed);

        As("fin1");
        var res = await Send(new DownloadAttachmentRequest(po.Id, _files, file.Id));
        res.IsNotFound.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task HiddenFieldException_StillHidden()
    {
        await Rig();
        await Permissions(new FieldPermissionDto(_r.Memo, FinanceGroup, FieldAccess.Hidden));
        As("buyer1");
        var po = await NewPo(_r, price: 30000, extra: V((_r.Memo, "內部備註")));
        await MustSubmit(po.Id, _r.Confirmed);
        var body = (DynamicRecordResponse)(await Get("fin1", po.Id)).Data!;
        body.Values.Should().NotContainKey($"f_{_r.Memo}");
        body.Values.Should().ContainKey($"f_{_r.Total}");
    }

    [Fact]
    public async Task AttachmentDownload_Granted()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r, price: 30000);
        var up = await Send(new UploadAttachmentRequest(po.Id, _files, new MemoryStream(Encoding.UTF8.GetBytes("收據")), "receipt.txt", 6));
        up.IsCreated.Should().BeTrue(Dump(up));
        var file = (UploadedAttachment)up.Data!;
        await MustSubmit(po.Id, _r.Confirmed);

        As("fin1");
        var res = await Send(new DownloadAttachmentRequest(po.Id, _files, file.Id));
        res.IsSuccess.Should().BeTrue(Dump(res));
        await using (((AttachmentDownload)res.Data!).Content) { }

        As("emp1");   // 沒有任務、沒有模組權限：與不存在相同的 404（a3d 審查修正）
        (await Send(new DownloadAttachmentRequest(po.Id, _files, file.Id))).IsNotFound.Should().BeTrue();
    }

    // ───────── 同意端與檢視端共用同一個欄位隱藏來源（審查 M2、裁決 U-A）─────────

    private Task<ServiceResult> Approve(long instanceId, string user, Dictionary<string, JsonElement?>? values)
    {
        As(user);
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);
        return Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, values));
    }

    [Fact]
    public async Task GrantedApprover_EditableAmount_ListedAndApproveSucceeds()
    {
        await Rig(editableAmount: true);
        var (po, id) = await Pending();

        As("fin1");
        var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(po))).Data!;
        view.Current!.Stages.Single(s => s.StageCode == "fin").EditableFieldKeys.Should().Equal($"f_{_amount}");

        var res = await Approve(id, "fin1", V((_amount, 28000)));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Rec(po).Values[$"f_{_amount}"]!.Value.GetDecimal().Should().Be(28000);
    }

    [Fact]
    public async Task GrantedApprover_FieldOutsideEditable_StillRejected()
    {
        await Rig(editableAmount: true);
        var (po, id) = await Pending();

        var res = await Approve(id, "fin1", V((_r.Memo, "偷改")));

        res.Errors[$"f_{_r.Memo}"].Should().Equal(ApprovalMessages.NotEditableField.Resolve(null));
        Rec(po).Values.ContainsKey($"f_{_r.Memo}").Should().BeFalse();
    }

    [Fact]
    public async Task GrantedApprover_AmountHiddenByFieldException_ApproveWithItStill400()
    {
        // 限制優先：簽核人自己的群組把金額設為隱藏，臨時讀取也看不到 → 檢視端不列、同意端拒絕，兩邊一致
        await Rig(editableAmount: true);
        var (po, id) = await Pending();
        var finance = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "finance");   // 見 ApprovalTestBase 建立的財務群組
        await Permissions(new FieldPermissionDto(_amount, finance.Id, FieldAccess.Hidden));

        As("fin1");
        var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(po))).Data!;
        view.Current!.Stages.Single(s => s.StageCode == "fin").EditableFieldKeys.Should().BeEmpty();
        var res = await Approve(id, "fin1", V((_amount, 28000)));
        res.Errors[$"f_{_amount}"].Should().Equal(ApprovalMessages.FieldHiddenForApprover.Resolve(null));
    }
}
