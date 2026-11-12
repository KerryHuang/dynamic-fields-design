using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>附件上傳與簽核（a2d2）：上傳走記錄寫入管線（後盾、事件、檢查）；收檔前的資格檢查擋簽核中、鎖定與已記帳。</summary>
public class ApprovalAttachmentTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private ApprovalFlow _flow = null!;
    private long _poFiles, _extraFiles;

    /// <summary>草稿 → 已確認 需簽核；單據與追加費用模組各有一個附件欄位。</summary>
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _poFiles = (await MustAdd(_r.Module, FieldType.Attachment, label: "附件")).Id;
        _extraFiles = (await MustAdd(_r.Extra, FieldType.Attachment, label: "附件")).Id;
        _flow = await SeedFlow(_r, "po-f", _r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var wf = await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial),
            Plain(_r.Partial, _r.Received), Plain(_r.Confirmed, _r.Cancelled)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(_r.Module, _r.Extra);
    }

    private static MemoryStream Bytes(string s = "x") => new(Encoding.UTF8.GetBytes(s));

    private Task<ServiceResult> Upload(long record, long field, string name = "a.txt", string content = "x") =>
        Send(new UploadAttachmentRequest(record, field, Bytes(content), name, content.Length));

    private int FilesOf(long record, long field) =>
        Rec(record).Values.TryGetValue($"f_{field}", out var v) && v is { ValueKind: JsonValueKind.Array } a ? a.GetArrayLength() : 0;

    private async Task<long> NewExtraId(long poId)
    {
        var title = (await Detail(_r.Extra)).TitleFieldId!.Value;
        var res = await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(
            _r.Extra, V((title, "追加"), (_r.ExtraRef, poId), (_r.ExtraAmount, 100))));
        res.IsCreated.Should().BeTrue(Dump(res));
        return ((DynamicRecordResponse)res.Data!).Id;
    }

    [Fact]
    public void Upload_IsRecordWriteRequest() =>
        typeof(UploadAttachmentRequest).Should().Implement<IRecordWriteRequest>();

    [Fact]
    public async Task Upload_EditableRecord_StillSucceeds()
    {
        await Rig();
        var po = await NewPo(_r);
        var res = await Upload(po.Id, _poFiles);
        res.IsCreated.Should().BeTrue(Dump(res));
        FilesOf(po.Id, _poFiles).Should().Be(1);
    }

    [Fact]
    public async Task Upload_EventWritesPendingOtherRecord_Returns409()
    {
        await Rig();
        // 追加費用記錄更新時，以系統身分改它所屬的單據備註；單據簽核中
        (await Send(new CreateEventRequest(Event("extra-touch", _r.Extra, new EventTrigger("update", null, null, null),
            UpdateVia("memo", _r.Module, _r.ExtraRef, Const(_r.Memo, "\"x\"")))))).IsCreated.Should().BeTrue();
        var po = await NewPo(_r);
        var extra = await NewExtraId(po.Id);
        await SeedInstance(_r, _flow, recordId: po.Id);

        var res = await Upload(extra, _extraFiles);

        res.IsConflict.Should().BeTrue(Dump(res));
        FilesOf(extra, _extraFiles).Should().Be(0);
    }

    [Fact]
    public async Task Upload_TriggersRecordUpdatedEvent()
    {
        await Rig();
        (await Send(new CreateEventRequest(Event("extra-touch", _r.Extra, new EventTrigger("update", null, null, null),
            UpdateVia("memo", _r.Module, _r.ExtraRef, Const(_r.Memo, "\"touched\"")))))).IsCreated.Should().BeTrue();
        var po = await NewPo(_r);
        var extra = await NewExtraId(po.Id);

        (await Upload(extra, _extraFiles)).IsCreated.Should().BeTrue();

        Rec(po.Id).Values[$"f_{_r.Memo}"]!.Value.GetString().Should().Be("touched");
    }

    private Task<ServiceResult> CheckUpload(long record, long field) => Send(new CheckAttachmentUploadRequest(record, field));

    /// <summary>建單（附一個檔案）、送簽、簽核人同意；單據停在已確認（鎖定）。</summary>
    private async Task<DynamicRecordResponse> ApprovedPoWithFile()
    {
        await Rig();
        As("buyer1");
        var po = await NewPo(_r);
        (await Upload(po.Id, _poFiles)).IsCreated.Should().BeTrue();
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        As("admin");
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
        return po;
    }

    [Fact]
    public async Task CheckUpload_PendingRecord_Returns409()
    {
        await Rig();
        var po = await NewPo(_r);
        await SeedInstance(_r, _flow, recordId: po.Id);

        var res = await CheckUpload(po.Id, _poFiles);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.Pending.Resolve(null));
    }

    [Fact]
    public async Task CheckUpload_LockedState_Returns409()
    {
        var po = await ApprovedPoWithFile();

        var res = await CheckUpload(po.Id, _poFiles);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Contain("附件不能再增減");
        (await Upload(po.Id, _poFiles, "b.txt")).IsConflict.Should().BeTrue();
        FilesOf(po.Id, _poFiles).Should().Be(1);
    }

    [Fact]
    public async Task CheckUpload_LockedStateBackToDraft_Succeeds()
    {
        var po = await ApprovedPoWithFile();
        (await Put(po.Id, new() { [$"f_{_r.Status}"] = JsonSerializer.SerializeToElement($"opt_{_r.Draft}") })).IsSuccess.Should().BeTrue();

        (await CheckUpload(po.Id, _poFiles)).IsSuccess.Should().BeTrue();
        (await Upload(po.Id, _poFiles, "b.txt")).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task CheckUpload_PostedRecord_Returns409()
    {
        await NewModule("pf-log", activate: false);
        await MustAdd("pf-log", FieldType.Decimal, label: "數量");
        (await Send(new ActivateModuleRequest("pf-log"))).IsSuccess.Should().BeTrue();
        await NewModule("pf-src");
        var files = await MustAdd("pf-src", FieldType.Attachment, label: "附件");
        (await Send(new ActivateModuleRequest("pf-src"))).IsSuccess.Should().BeTrue();
        (await Send(new CreateEventRequest(Event("pf-post", "pf-src", new EventTrigger("create", null, null, null),
            Create("post", "pf-log", null, Const((await Detail("pf-log")).TitleFieldId!.Value, "\"x\"")))))).IsCreated.Should().BeTrue();
        var created = await Send(new CreateRecordRequest("pf-src", V(((await Detail("pf-src")).TitleFieldId!.Value, "單")), null, null));
        created.IsCreated.Should().BeTrue(Dump(created));
        var id = ((DynamicRecordResponse)created.Data!).Id;

        var res = await CheckUpload(id, files.Id);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be("已記帳的單據不能再增減附件");
    }

    [Fact]
    public async Task CheckUpload_LedgerRecord_Returns409()
    {
        await NewModule("pf-led");
        var rev = await MustAdd("pf-led", FieldType.Reference, RefTo("pf-led") with { ReadOnly = true }, "沖銷自");
        var qty = await MustAdd("pf-led", FieldType.Decimal, new FieldConfig { DefaultValue = J("0") }, "數量");
        var files = await MustAdd("pf-led", FieldType.Attachment, label: "附件");
        (await Send(new ActivateModuleRequest("pf-led"))).IsSuccess.Should().BeTrue();
        (await Send(new SaveLedgerRequest("pf-led", new LedgerInput(rev.Id, [qty.Id], [], false)))).IsSuccess.Should().BeTrue();
        var created = await Send(new CreateRecordRequest("pf-led", V(((await Detail("pf-led")).TitleFieldId!.Value, "帳"), (qty.Id, 5)), null, null));
        created.IsCreated.Should().BeTrue(Dump(created));

        var res = await CheckUpload(((DynamicRecordResponse)created.Data!).Id, files.Id);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be("已記帳的單據不能再增減附件");
    }

    [Fact]
    public async Task Put_RemoveAttachment_LockedState_Returns400()
    {
        var po = await ApprovedPoWithFile();

        var res = await Put(po.Id, new() { [$"f_{_poFiles}"] = JsonSerializer.SerializeToElement(Array.Empty<object>()) });

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey($"f_{_poFiles}");
        FilesOf(po.Id, _poFiles).Should().Be(1);
    }

    [Fact]
    public async Task Put_RemoveAttachment_Pending_Returns409()
    {
        await Rig();
        var po = await NewPo(_r);
        (await Upload(po.Id, _poFiles)).IsCreated.Should().BeTrue();
        await SeedInstance(_r, _flow, recordId: po.Id);

        var res = await Put(po.Id, new() { [$"f_{_poFiles}"] = JsonSerializer.SerializeToElement(Array.Empty<object>()) });

        res.IsConflict.Should().BeTrue(Dump(res));
    }
}
