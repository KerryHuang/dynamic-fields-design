using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 1：新增、修改、刪除、明細列、SetNull 清空各寫哪些歷程列。</summary>
public class RecordHistoryWriteTests : DesignerTestBase
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0);

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private static DynamicRecordResponse Ok(ServiceResult r)
    {
        (r.IsSuccess || r.IsCreated).Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private Task<List<RecordHistory>> History(long recordId) =>
        Db.Context.Set<RecordHistory>().AsNoTracking().Where(h => h.RecordId == recordId).OrderBy(h => h.Id).ToListAsync();

    private Task<string> RowVersion(long id) =>
        Db.Context.Set<Record>().AsNoTracking().Where(r => r.Id == id).Select(r => r.RowVersion).SingleAsync();

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    /// <summary>模組：標題（text）＋備註（text）。</summary>
    private async Task<(string Title, FieldResponse Note)> Simple()
    {
        await NewModule("memo");
        var note = await MustAdd("memo", FieldType.Text, label: "備註");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        return (await TitleKey("memo"), note);
    }

    private async Task<DynamicRecordResponse> Save(long id, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null) =>
        Ok(await Send(new UpdateRecordRequest(id, values, await RowVersion(id), details)));

    [Fact]
    public async Task Create_WritesOneCreateRow()
    {
        var (title, note) = await Simple();
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (note.Key, "x")))));

        var rows = await History(rec.Id);
        rows.Should().ContainSingle();
        rows[0].Operation.Should().Be(RecordHistoryOperation.Create);
        rows[0].FieldId.Should().BeNull();
        rows[0].OldValue.Should().BeNull();
        rows[0].NewValue.Should().BeNull();
        rows[0].ChangedUserId.Should().Be("admin");
        rows[0].ChangedTime.Should().Be(Now);
    }

    [Fact]
    public async Task Update_WritesOnlyChangedFields()
    {
        var (title, note) = await Simple();
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (note.Key, "x")))));

        await Save(rec.Id, Vals((title, "b"), (note.Key, "x")));

        var updates = (await History(rec.Id)).Where(h => h.Operation == RecordHistoryOperation.Update).ToList();
        updates.Should().ContainSingle();
        updates[0].FieldId.Should().Be((await Detail("memo")).TitleFieldId);
        updates[0].OldValue.Should().Be("\"a\"");
        updates[0].NewValue.Should().Be("\"b\"");
        updates[0].ChangedUserId.Should().Be("admin");
    }

    [Fact]
    public async Task Update_NothingChanged_WritesNothing()
    {
        var (title, note) = await Simple();
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (note.Key, "x")))));

        await Save(rec.Id, Vals((title, "a"), (note.Key, "x")));

        (await History(rec.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create);
    }

    [Fact]
    public async Task Update_ClearingValue_WritesNullNew()
    {
        var (title, note) = await Simple();
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (note.Key, "x")))));

        await Save(rec.Id, Vals((note.Key, null)));

        var update = (await History(rec.Id)).Single(h => h.Operation == RecordHistoryOperation.Update);
        update.FieldId.Should().Be(note.Id);
        update.OldValue.Should().Be("\"x\"");
        update.NewValue.Should().BeNull();
    }

    [Fact]
    public async Task Update_SkipsFormulaRollupAutonumber()
    {
        await NewModule("memo");
        var qty = await MustAdd("memo", FieldType.Decimal, new FieldConfig { Scale = 2 }, "數量");
        await MustAdd("memo", FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}} * 2", ResultType = "number", Scale = 2 }, "兩倍");
        await MustAdd("memo", FieldType.AutoNumber, new FieldConfig { Format = "N-{000}" }, "編號");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (qty.Key, 1)))));

        await Save(rec.Id, Vals((qty.Key, 2)));

        var updates = (await History(rec.Id)).Where(h => h.Operation == RecordHistoryOperation.Update).ToList();
        updates.Should().ContainSingle();
        updates[0].FieldId.Should().Be(qty.Id);
    }

    [Fact]
    public async Task Delete_WritesDeleteRow_ForRootAndCascaded()
    {
        await NewModule("parent", activate: true);
        await NewModule("child");
        var refField = await MustAdd("child", FieldType.Reference, RefTo("parent", onDelete: ReferenceOnDelete.Cascade), "上層");
        (await Send(new ActivateModuleRequest("child"))).IsSuccess.Should().BeTrue();
        var p = Ok(await Send(new CreateRecordRequest("parent", Vals((await TitleKey("parent"), "p")))));
        var c = Ok(await Send(new CreateRecordRequest("child", Vals((await TitleKey("child"), "c"), (refField.Key, p.Id)))));

        (await Send(new DeleteRecordRequest(p.Id, await RowVersion(p.Id)))).IsSuccess.Should().BeTrue();

        (await History(p.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create, RecordHistoryOperation.Delete);
        (await History(c.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create, RecordHistoryOperation.Delete);
        (await History(c.Id)).Last().FieldId.Should().BeNull();
    }

    [Fact]
    public async Task Delete_SetNull_WritesUpdateOnClearedSource()
    {
        await NewModule("target", activate: true);
        await NewModule("source");
        var refField = await MustAdd("source", FieldType.Reference, RefTo("target", onDelete: ReferenceOnDelete.SetNull), "參照");
        (await Send(new ActivateModuleRequest("source"))).IsSuccess.Should().BeTrue();
        var sales = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = (await Detail("target")).ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        });
        await Db.Context.SaveChangesAsync();

        var (targetTitle, sourceTitle) = (await TitleKey("target"), await TitleKey("source"));

        As("sales01");
        var t = Ok(await Send(new CreateRecordRequest("target", Vals((targetTitle, "t")))));
        As("admin");
        var s = Ok(await Send(new CreateRecordRequest("source", Vals((sourceTitle, "s"), (refField.Key, t.Id)))));
        var stored = StoredRaw(await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == s.Id), refField.Key);

        As("sales01");
        var deleted = await Send(new DeleteRecordRequest(t.Id, await RowVersion(t.Id)));
        deleted.IsSuccess.Should().BeTrue(deleted.Message);

        var update = (await History(s.Id)).Single(h => h.Operation == RecordHistoryOperation.Update);
        update.FieldId.Should().Be(refField.Id);
        update.OldValue.Should().Be(stored);
        update.NewValue.Should().BeNull();
        update.ChangedUserId.Should().Be("sales01");
        (await History(t.Id)).Last().Operation.Should().Be(RecordHistoryOperation.Delete);
    }

    private static string StoredRaw(Record r, string key) =>
        JsonDocument.Parse(r.ValuesJson).RootElement.GetProperty(key).GetRawText();

    [Fact]
    public async Task DetailRows_WriteUnderRowRecordId()
    {
        await NewModule("order");
        await NewModule("line");
        var parentField = await MustAdd("line", FieldType.Reference, RefTo("order", ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd("line", FieldType.Decimal, new FieldConfig { Scale = 2 }, "數量");
        await MustAdd("order", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentField.Id, RollupFunction.Sum, qty.Id, null) }, "合計");
        (await Send(new ActivateModuleRequest("order"))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest("line"))).IsSuccess.Should().BeTrue();
        var orderTitle = await TitleKey("order");
        var lineTitle = await TitleKey("line");
        DetailRowInput NewRow(decimal q) => new(null, Vals((lineTitle, "品項"), (qty.Key, q)), null, false);

        var header = Ok(await Send(new CreateRecordRequest("order", Vals((orderTitle, "訂單")), null,
            new() { ["line"] = [NewRow(1), NewRow(2)] })));
        var (keep, drop) = (header.Details!["line"][0], header.Details!["line"][1]);
        (await History(keep.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create);

        var saved = await Save(header.Id, [], new()
        {
            ["line"] =
            [
                new DetailRowInput(keep.Id, Vals((qty.Key, 5)), keep.RowVersion, false),
                new DetailRowInput(drop.Id, null, drop.RowVersion, true),
                NewRow(3)
            ]
        });

        var kept = await History(keep.Id);
        kept.Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create, RecordHistoryOperation.Update);
        kept[1].FieldId.Should().Be(qty.Id);
        (await History(drop.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create, RecordHistoryOperation.Delete);
        var added = saved.Details!["line"].Single(l => l.Id != keep.Id);
        (await History(added.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create);
        // 表頭只有合計（彙總）變了：不寫歷程
        (await History(header.Id)).Select(h => h.Operation).Should().Equal(RecordHistoryOperation.Create);
    }

    [Fact]
    public async Task FailedSave_WritesNothing()
    {
        await NewModule("memo");
        var qty = await MustAdd("memo", FieldType.Decimal, new FieldConfig { Scale = 2 }, "數量");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (qty.Key, 1)))));

        var created = await Send(new CreateRecordRequest("memo", Vals((title, "b"), (qty.Key, "abc"))));
        var updated = await Send(new UpdateRecordRequest(rec.Id, Vals((title, "c"), (qty.Key, "abc")), await RowVersion(rec.Id)));

        created.IsSuccess.Should().BeFalse();
        updated.IsSuccess.Should().BeFalse();
        (await Db.Context.Set<RecordHistory>().AsNoTracking().CountAsync()).Should().Be(1);
    }
}
