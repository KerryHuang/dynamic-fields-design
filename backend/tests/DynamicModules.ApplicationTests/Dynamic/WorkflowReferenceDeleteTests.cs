using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>刪除被參照的主檔時，連帶刪除或清空的單據若處於狀態機的唯讀狀態，整個刪除要被擋下（不能繞過唯讀）。</summary>
public class WorkflowReferenceDeleteTests : ConversionTestBase
{
    private static string Why(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + ":" + e.Value[0]));

    /// <summary>來源單 Src 另有一個指向往來對象的參照欄位（指定刪除行為）；回傳該欄位。</summary>
    private async Task<long> AddRefField(Chain c, ReferenceOnDelete onDelete) =>
        (await MustAdd(c.Src, FieldType.Reference, RefTo(c.Party, onDelete: onDelete), "連動往來")).Id;

    private async Task<DynamicRecordResponse> NewParty(Chain c, string name)
    {
        var r = await Send(new CreateRecordRequest(c.Party, new Dictionary<string, JsonElement?> { [K(c.PartyName)] = J($"\"{name}\"") }));
        r.IsCreated.Should().BeTrue(Why(r));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> NewDoc(Chain c, long refField, long partyId, bool approved)
    {
        var r = await Send(new CreateRecordRequest(c.Src, new Dictionary<string, JsonElement?>
        {
            [K(c.SrcTitle)] = J("\"S-9\""), [K(refField)] = J(partyId.ToString())
        }));
        r.IsCreated.Should().BeTrue(Why(r));
        var doc = (DynamicRecordResponse)r.Data!;
        return approved ? await Approve(c, doc) : doc;
    }

    private Task<Record> Stored(long id) => Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == id);

    [Theory]
    [InlineData(ReferenceOnDelete.Cascade)]
    [InlineData(ReferenceOnDelete.SetNull)]
    public async Task Delete_AMasterRecord_IsBlocked_WhenAReadOnlyStateDocumentWouldBeDeletedOrCleared(ReferenceOnDelete onDelete)
    {
        var c = await BuildChain("a");
        var field = await AddRefField(c, onDelete);
        var party = await NewParty(c, "往來乙");
        var doc = await NewDoc(c, field, party.Id, approved: true);

        var r = await Send(new DeleteRecordRequest(party.Id, party.RowVersion));
        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be("有處於唯讀狀態的單據參照這筆記錄，不能刪除");

        (await Stored(party.Id)).DeleteFlag.Should().BeFalse();
        var kept = await Stored(doc.Id);
        kept.DeleteFlag.Should().BeFalse();
        kept.RowVersion.Should().Be(doc.RowVersion); // 沒被動過（清空會換版本號）
        JsonDocument.Parse(kept.ValuesJson).RootElement.TryGetProperty(K(field), out var still).Should().BeTrue();
        still.GetInt64().Should().Be(party.Id);
    }

    [Fact]
    public async Task Delete_AMasterRecord_StillCascadesAndClears_WhenTheDocumentsAreNotLocked()
    {
        var c = await BuildChain("a");
        var cascade = await AddRefField(c, ReferenceOnDelete.Cascade);
        var party = await NewParty(c, "往來乙");
        var doc = await NewDoc(c, cascade, party.Id, approved: false);

        (await Send(new DeleteRecordRequest(party.Id, party.RowVersion))).IsSuccess.Should().BeTrue();
        (await Stored(doc.Id)).DeleteFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_AMasterRecord_WritesNothing_WhenOnlyOneOfTheAffectedDocumentsIsLocked()
    {
        var c = await BuildChain("a");
        var cascade = await AddRefField(c, ReferenceOnDelete.Cascade);
        var party = await NewParty(c, "往來乙");
        var open = await NewDoc(c, cascade, party.Id, approved: false);
        await NewDoc(c, cascade, party.Id, approved: true);

        var r = await Send(new DeleteRecordRequest(party.Id, party.RowVersion));
        r.IsConflict.Should().BeTrue(Why(r));
        (await Stored(open.Id)).DeleteFlag.Should().BeFalse(); // 沒鎖定的那張也沒被刪
        (await Stored(party.Id)).DeleteFlag.Should().BeFalse();
    }

    // ───────── 明細列：要看表頭單據的狀態 ─────────

    /// <summary>來源單的明細模組另有一個指向往來對象的參照欄位；建一張有一列明細的來源單（可核准）。</summary>
    private async Task<(long LineField, DynamicRecordResponse Doc, long LineId)> DocWithLine(Chain c, ReferenceOnDelete onDelete, long partyId, bool approved)
    {
        var field = (await MustAdd(c.SrcLine, FieldType.Reference, RefTo(c.Party, onDelete: onDelete), "連動往來")).Id;
        var row = new DetailRowInput(null, new Dictionary<string, JsonElement?>
        {
            [K(c.LineTitle)] = J("\"螺絲\""), [K(c.LineItem)] = J("\"螺絲\""), [K(c.LineQty)] = J("5"), [K(c.LinePrice)] = J("1"),
            [K(field)] = J(partyId.ToString())
        }, null);
        var r = await Send(new CreateRecordRequest(c.Src, new Dictionary<string, JsonElement?> { [K(c.SrcTitle)] = J("\"S-9\"") }, null,
            new Dictionary<string, List<DetailRowInput>> { [c.SrcLine] = [row] }));
        r.IsCreated.Should().BeTrue(Why(r));
        var doc = (DynamicRecordResponse)r.Data!;
        var lineId = doc.Details![c.SrcLine].Single().Id;
        return (field, approved ? await Approve(c, doc) : doc, lineId);
    }

    [Theory]
    [InlineData(ReferenceOnDelete.Cascade)]
    [InlineData(ReferenceOnDelete.SetNull)]
    public async Task Delete_AMasterRecord_IsBlocked_WhenTheDetailLinesHeaderIsInAReadOnlyState(ReferenceOnDelete onDelete)
    {
        var c = await BuildChain("a");
        var party = await NewParty(c, "往來乙");
        var (field, _, lineId) = await DocWithLine(c, onDelete, party.Id, approved: true);
        var before = await Stored(lineId);

        var r = await Send(new DeleteRecordRequest(party.Id, party.RowVersion));
        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be("有處於唯讀狀態的單據參照這筆記錄，不能刪除");

        (await Stored(party.Id)).DeleteFlag.Should().BeFalse();
        var line = await Stored(lineId);
        line.DeleteFlag.Should().BeFalse();
        line.RowVersion.Should().Be(before.RowVersion);
        JsonDocument.Parse(line.ValuesJson).RootElement.GetProperty(K(field)).GetInt64().Should().Be(party.Id);
    }

    [Theory]
    [InlineData(ReferenceOnDelete.Cascade, true)]
    [InlineData(ReferenceOnDelete.SetNull, false)]
    public async Task Delete_AMasterRecord_StillProcessesDetailLines_WhenTheHeaderIsNotLocked(ReferenceOnDelete onDelete, bool deleted)
    {
        var c = await BuildChain("a");
        var party = await NewParty(c, "往來乙");
        var (field, _, lineId) = await DocWithLine(c, onDelete, party.Id, approved: false);

        (await Send(new DeleteRecordRequest(party.Id, party.RowVersion))).IsSuccess.Should().BeTrue();
        var line = await Stored(lineId);
        line.DeleteFlag.Should().Be(deleted);
        if (!deleted) JsonDocument.Parse(line.ValuesJson).RootElement.TryGetProperty(K(field), out _).Should().BeFalse();
    }
}
