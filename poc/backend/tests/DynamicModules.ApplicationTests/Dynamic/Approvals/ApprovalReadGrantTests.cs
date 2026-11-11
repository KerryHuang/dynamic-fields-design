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
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _amount = (await MustAdd(_r.Module, FieldType.Currency, label: "金額")).Id;
        _files = (await MustAdd(_r.Module, FieldType.Attachment, label: "附件")).Id;
        await SeedFlow(_r, "po-g", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
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
        (await Get("emp1", po)).IsForbidden.Should().BeTrue();   // 沒有任務、沒有模組權限
    }

    [Fact]
    public async Task OtherRecord_StillDenied()
    {
        await Rig();
        await Pending();
        As("buyer1");
        var other = await NewPo(_r, price: 100);
        var res = await Get("fin1", other.Id);
        res.IsForbidden.Should().BeTrue(Dump(res));   // 待裁決 1：沒有模組 Read 維持 403
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
        (await Get("fin1", po)).IsForbidden.Should().BeTrue();
        (await Get("fin2", po)).IsForbidden.Should().BeTrue();   // 同關被關閉的任務一起收回
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
        (await Get("fin1", po)).IsForbidden.Should().BeTrue();
        (await Get("emp2", po)).IsSuccess.Should().BeTrue();
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
        var up = await Send(new UploadAttachmentRequest(po.Id, _files, new MemoryStream(Encoding.UTF8.GetBytes("收據")), "receipt.txt"));
        up.IsCreated.Should().BeTrue(Dump(up));
        var file = (UploadedAttachment)up.Data!;
        await MustSubmit(po.Id, _r.Confirmed);

        As("fin1");
        var res = await Send(new DownloadAttachmentRequest(po.Id, _files, file.Id));
        res.IsSuccess.Should().BeTrue(Dump(res));
        await using (((AttachmentDownload)res.Data!).Content) { }

        As("emp1");   // 沒有任務、沒有模組權限
        (await Send(new DownloadAttachmentRequest(po.Id, _files, file.Id))).IsForbidden.Should().BeTrue();
    }
}
