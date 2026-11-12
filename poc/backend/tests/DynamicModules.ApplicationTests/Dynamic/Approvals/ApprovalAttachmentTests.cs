using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
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
}
