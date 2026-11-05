using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>表頭與明細一起存檔：order（表頭：單號、合計、筆數、稅額）＋ line（明細：數量、單價、金額、項次）。</summary>
public class DetailSaveTests : DesignerTestBase
{
    private sealed record S(
        string Parent, string Child, string ParentTitle, string ChildTitle, FieldResponse ParentField,
        FieldResponse Qty, FieldResponse Price, FieldResponse Amount, FieldResponse LineNo,
        FieldResponse OrderNo, FieldResponse Total, FieldResponse Count, FieldResponse Tax);

    private async Task<S> Arrange(bool pricing = true, bool edit = true)
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        var parentField = await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd(child, FieldType.Decimal, new FieldConfig { Scale = 2 }, "數量");
        var price = await MustAdd(child, FieldType.Currency, label: "單價");
        var amount = await MustAdd(child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{qty.Id}}} * {{{price.Id}}}", ResultType = "currency", Scale = 2 }, "金額");
        var lineNo = await MustAdd(child, FieldType.AutoNumber, new FieldConfig { Format = "{0}", Scope = "parent" }, "項次");
        var orderNo = await MustAdd(parent, FieldType.AutoNumber, new FieldConfig { Format = "SO-{000}" }, "單號");
        var total = await MustAdd(parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentField.Id, RollupFunction.Sum, amount.Id, null) }, "合計");
        var count = await MustAdd(parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentField.Id, RollupFunction.Count, null, null) }, "筆數");
        var tax = await MustAdd(parent, FieldType.Formula,
            new FieldConfig { Formula = $"ROUND({{{total.Id}}} * 5 / 100, 0)", ResultType = "currency", Scale = 0 }, "稅額");
        (await Send(new ActivateModuleRequest(parent))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(child))).IsSuccess.Should().BeTrue();

        var sales = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = (await Detail(parent)).ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = edit, CanDelete = true, CanAmt = pricing
        });
        await Db.Context.SaveChangesAsync();
        return new S(parent, child, $"f_{(await Detail(parent)).TitleFieldId}", $"f_{(await Detail(child)).TitleFieldId}",
            parentField, qty, price, amount, lineNo, orderNo, total, count, tax);
    }

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private static DetailRowInput NewRow(S s, decimal qty, decimal price) =>
        new(null, Vals((s.ChildTitle, "品項"), (s.Qty.Key, qty), (s.Price.Key, price)), null, false);

    private static Dictionary<string, List<DetailRowInput>> Rows(S s, params DetailRowInput[] rows) => new() { [s.Child] = [.. rows] };

    private async Task<ServiceResult> Create(S s, params DetailRowInput[] rows) =>
        await Send(new CreateRecordRequest(s.Parent, Vals((s.ParentTitle, "訂單")), null, Rows(s, rows)));

    private async Task<DynamicRecordResponse> MustCreate(S s, params DetailRowInput[] rows)
    {
        var r = await Create(s, rows);
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<ServiceResult> Update(DynamicRecordResponse header, Dictionary<string, List<DetailRowInput>>? details,
        Dictionary<string, JsonElement?>? values = null) =>
        await Send(new UpdateRecordRequest(header.Id, values ?? [], header.RowVersion, details));

    private static DynamicRecordResponse Ok(ServiceResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Get(long id) => Ok(await Send(new GetRecordRequest(id)));

    private static IReadOnlyList<DynamicRecordResponse> Lines(DynamicRecordResponse header, S s) => header.Details![s.Child];

    private static decimal? Num(DynamicRecordResponse r, FieldResponse f) =>
        r.Values.TryGetValue(f.Key, out var v) && v is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : null;

    private static string? Str(DynamicRecordResponse r, FieldResponse f) =>
        r.Values.TryGetValue(f.Key, out var v) && v is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;

    private static DetailRowInput Edit(DynamicRecordResponse row, params (string Key, object? Value)[] values) =>
        new(row.Id, Vals(values), row.RowVersion, false);

    private static DetailRowInput Remove(DynamicRecordResponse row) => new(row.Id, null, row.RowVersion, true);

    private async Task<int> CountRows(string module)
    {
        var id = (await Detail(module)).Id;
        return await Db.Context.Set<Record>().AsNoTracking().CountAsync(r => r.ModuleId == id);
    }

    // ---- 新增 ----

    [Fact]
    public async Task Create_WithDetails_SavesAllInOneRequest()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10), NewRow(s, 3, 5), NewRow(s, 1, 100));

        var lines = Lines(header, s);
        lines.Should().HaveCount(3);
        lines.Select(l => Str(l, s.LineNo)).Should().Equal("1", "2", "3");
        lines.Select(l => Num(l, s.Amount)).Should().Equal(20m, 15m, 100m);
        Num(header, s.Total).Should().Be(135m);
        Num(header, s.Count).Should().Be(3m);
        Num(header, s.Tax).Should().Be(7m);
        Str(header, s.OrderNo).Should().Be("SO-001");
    }

    [Fact]
    public async Task Create_DetailRowInvalid_NothingSaved()
    {
        var s = await Arrange();
        var bad = new DetailRowInput(null, Vals((s.ChildTitle, "品項"), (s.Qty.Key, "abc")), null, false);

        var r = await Create(s, NewRow(s, 1, 1), bad, NewRow(s, 2, 2));

        r.Errors.Should().ContainKey($"details.{s.Child}[1].{s.Qty.Key}");
        (await CountRows(s.Parent)).Should().Be(0);
        (await CountRows(s.Child)).Should().Be(0);
        Str(await MustCreate(s, NewRow(s, 1, 1)), s.OrderNo).Should().Be("SO-001");
    }

    [Fact]
    public async Task Create_ParentFieldInRow_Ignored()
    {
        var s = await Arrange();
        var other = await MustCreate(s, NewRow(s, 1, 1));
        var row = new DetailRowInput(null,
            Vals((s.ChildTitle, "品項"), (s.Qty.Key, 1), (s.ParentField.Key, other.Id)), null, false);

        var header = await MustCreate(s, row);

        Num(Lines(header, s).Single(), s.ParentField).Should().Be(header.Id);
        Lines(await Get(other.Id), s).Should().HaveCount(1);
    }

    [Fact]
    public async Task Create_DetailRowOwner_IsParentOwner()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 1, 1));
        Lines(header, s).Single().OwnerUserId.Should().Be("admin");

        As("sales01");
        var updated = Ok(await Update(header, Rows(s, NewRow(s, 2, 2))));

        Lines(updated, s).Select(l => l.OwnerUserId).Should().AllBe("admin");
        Lines(updated, s)[1].CreatedUserId.Should().Be("sales01");
    }

    // ---- 修改明細列 ----

    [Fact]
    public async Task Update_ChangeRowQty_RollupUpdated()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10), NewRow(s, 3, 5), NewRow(s, 1, 100));
        var second = Lines(header, s)[1];

        var updated = Ok(await Update(header, Rows(s, Edit(second, (s.Qty.Key, 5)))));

        Num(Lines(updated, s)[1], s.Amount).Should().Be(25m);
        Num(Lines(updated, s)[1], s.Price).Should().Be(5m);
        Num(updated, s.Total).Should().Be(145m);
        updated.RowVersion.Should().NotBe(header.RowVersion);
        Lines(updated, s)[1].RowVersion.Should().NotBe(second.RowVersion);
    }

    [Fact]
    public async Task Update_DeleteRow_RollupUpdated_NumberNotReused()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10), NewRow(s, 3, 5), NewRow(s, 1, 100));

        var updated = Ok(await Update(header, Rows(s, Remove(Lines(header, s)[1]), NewRow(s, 1, 1))));

        Lines(updated, s).Select(l => Str(l, s.LineNo)).Should().Equal("1", "3", "4");
        Num(updated, s.Count).Should().Be(3m);
        Num(updated, s.Total).Should().Be(121m);
    }

    [Fact]
    public async Task Update_AddRow_OtherRowsUntouched()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10), NewRow(s, 3, 5));

        var updated = Ok(await Update(header, Rows(s, NewRow(s, 1, 1))));

        Lines(updated, s).Should().HaveCount(3);
        Lines(updated, s).Take(2).Select(l => l.RowVersion).Should().Equal(Lines(header, s).Select(l => l.RowVersion));
    }

    [Fact]
    public async Task Update_DetailRowInvalid_NothingSaved()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10), NewRow(s, 3, 5), NewRow(s, 1, 100));
        var lines = Lines(header, s);

        var r = await Update(header,
            Rows(s, Edit(lines[0], (s.Qty.Key, 9)), Edit(lines[1], (s.Qty.Key, "abc")), NewRow(s, 1, 1)),
            Vals((s.ParentTitle, "改過")));

        r.Errors.Keys.Should().Equal($"details.{s.Child}[1].{s.Qty.Key}");
        var reloaded = await Get(header.Id);
        reloaded.Title.Should().Be("訂單");
        reloaded.RowVersion.Should().Be(header.RowVersion);
        Lines(reloaded, s).Select(l => l.RowVersion).Should().Equal(lines.Select(l => l.RowVersion));
        Lines(reloaded, s).Select(l => Num(l, s.Qty)).Should().Equal(2m, 3m, 1m);
    }

    // ---- 版本與歸屬 ----

    [Fact]
    public async Task Update_StaleRowVersion_Conflict()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 2, 10));
        var row = Lines(header, s)[0];

        var r = await Update(header, Rows(s, new DetailRowInput(row.Id, Vals((s.Qty.Key, 3)), "stale", false)));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("資料已被其他人修改，請重新載入");
    }

    [Fact]
    public async Task Update_RowOfOtherParent_Rejected()
    {
        var s = await Arrange();
        var a = await MustCreate(s, NewRow(s, 1, 1));
        var b = await MustCreate(s, NewRow(s, 2, 2));

        var r = await Update(a, Rows(s, Edit(Lines(b, s)[0], (s.Qty.Key, 9))));

        Err(r, $"details.{s.Child}[0]._record").Should().Be("明細不存在");
    }

    [Fact]
    public async Task Update_UnknownDetailModule_Rejected()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 1, 1));

        var r = await Update(header, new Dictionary<string, List<DetailRowInput>> { ["foo"] = [NewRow(s, 1, 1)] });

        Err(r, "details.foo").Should().Be("明細模組不存在或未啟用");
    }

    [Fact]
    public async Task Update_Over200Rows_Rejected()
    {
        var s = await Arrange();
        var header = await MustCreate(s, Enumerable.Range(0, 200).Select(_ => NewRow(s, 1, 1)).ToArray());
        Lines(header, s).Should().HaveCount(200);

        var r = await Update(header, Rows(s, NewRow(s, 1, 1)));

        Err(r, $"details.{s.Child}").Should().Be("明細最多 200 列");
        // 刪一列再加一列：存檔後仍是 200 列，可以存
        Ok(await Update(header, Rows(s, Remove(Lines(header, s)[0]), NewRow(s, 1, 1))));
    }

    // ---- 唯讀規則 ----

    [Fact]
    public async Task Update_HeaderReadOnlyAll_DetailChangeRejected()
    {
        var s = await Arrange();
        var m = await Detail(s.Parent);
        var status = (FieldResponse)(await Send(new Application.Dynamic.FieldService.Commands.AddField.AddFieldRequest(
            s.Parent, m.Blocks[0].Id, FieldType.Enum, Text("狀態"), new FieldConfig()))).Data!;
        var closed = (OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("已結案"), null))).Data!;
        (await Send(new SaveRulesRequest(s.Parent, [new RuleInput(RuleKind.ReadOnly,
            J($"{{\"all\":[{{\"field\":{status.Id},\"op\":\"eq\",\"value\":\"{closed.Value}\"}}]}}"), ["*"], null, null, true)])))
            .IsSuccess.Should().BeTrue();
        var created = await Send(new CreateRecordRequest(s.Parent,
            Vals((s.ParentTitle, "訂單"), (status.Key, closed.Value)), null, Rows(s, NewRow(s, 1, 1))));
        var header = (DynamicRecordResponse)created.Data!;

        var r = await Update(header, Rows(s, Edit(Lines(header, s)[0], (s.Qty.Key, 9))));

        r.Errors.Keys.Should().Equal("_record");
        Err(r, "_record").Should().Be("此記錄目前不可修改");
    }

    [Fact]
    public async Task Update_RowReadOnlyAll_RowRejected()
    {
        var s = await Arrange();
        (await Send(new SaveRulesRequest(s.Child, [new RuleInput(RuleKind.ReadOnly,
            J($"{{\"all\":[{{\"field\":{s.Qty.Id},\"op\":\"gt\",\"value\":5}}]}}"), ["*"], null, null, true)])))
            .IsSuccess.Should().BeTrue();
        var header = await MustCreate(s, NewRow(s, 10, 1), NewRow(s, 1, 1));
        var lines = Lines(header, s);

        var edit = await Update(header, Rows(s, Edit(lines[0], (s.Qty.Key, 1)), Edit(lines[1], (s.Qty.Key, 2))));
        edit.Errors.Keys.Should().Equal($"details.{s.Child}[0]._record");
        Err(edit, $"details.{s.Child}[0]._record").Should().Be("此記錄目前不可修改");

        var delete = await Update(header, Rows(s, Remove(lines[0])));
        Err(delete, $"details.{s.Child}[0]._record").Should().Be("此記錄目前不可修改");
    }

    // ---- 權限 ----

    [Fact]
    public async Task Update_NoPricing_DetailCurrencyIgnored()
    {
        var s = await Arrange(pricing: false);
        var header = await MustCreate(s, NewRow(s, 2, 10));

        As("sales01");
        var updated = Ok(await Update(header,
            Rows(s, Edit(Lines(header, s)[0], (s.Qty.Key, 3), (s.Price.Key, 99)), NewRow(s, 1, 50))));

        updated.Values.Should().NotContainKeys(s.Total.Key, s.Tax.Key);
        foreach (var line in Lines(updated, s))
            line.Values.Should().NotContainKeys(s.Price.Key, s.Amount.Key);

        As("admin");
        var stored = await Get(header.Id);
        Lines(stored, s).Select(l => Num(l, s.Price)).Should().Equal(10m, null);
        Num(Lines(stored, s)[0], s.Amount).Should().Be(30m);
        Num(stored, s.Total).Should().Be(30m);
    }

    [Fact]
    public async Task Update_NoEdit_Forbidden()
    {
        var s = await Arrange(edit: false);
        var header = await MustCreate(s, NewRow(s, 2, 10));

        As("sales01");
        var r = await Update(header, Rows(s, Edit(Lines(header, s)[0], (s.Qty.Key, 3))));

        r.IsForbidden.Should().BeTrue();
    }

    // ---- 讀取與刪除 ----

    [Fact]
    public async Task Get_Parent_IncludesDetailsOrderedById()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 1, 1), NewRow(s, 2, 2), NewRow(s, 3, 3));

        var got = await Get(header.Id);

        var lines = Lines(got, s);
        lines.Select(l => l.Id).Should().BeInAscendingOrder().And.HaveCount(3);
        lines.Select(l => l.ModuleKey).Should().AllBe(s.Child);
        lines[0].References.Should().ContainKey(s.ParentField.Key);

        var plain = await NewModule("plain", activate: true);
        var plainRecord = Ok(await Send(new CreateRecordRequest(plain,
            Vals(($"f_{(await Detail(plain)).TitleFieldId}", "x")))));
        plainRecord.Details.Should().BeNull();
        (await Get(plainRecord.Id)).Details.Should().BeNull();
    }

    [Fact]
    public async Task Delete_Parent_CascadesDetails()
    {
        var s = await Arrange();
        var header = await MustCreate(s, NewRow(s, 1, 1), NewRow(s, 2, 2));

        Ok(await Send(new DeleteRecordRequest(header.Id, header.RowVersion)));

        var ids = Lines(header, s).Select(l => l.Id).ToList();
        (await Db.Context.Set<Record>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.DeleteFlag).ToListAsync())
            .Should().Equal(true, true);
    }

    // ---- 公式與規則引用彙總 ----

    [Fact]
    public async Task Validation_OnRollup_SeesNewValue()
    {
        var s = await Arrange();
        var titleId = (await Detail(s.Parent)).TitleFieldId!.Value;
        (await Send(new SaveRulesRequest(s.Parent, [new RuleInput(RuleKind.Validation,
            J($"{{\"all\":[{{\"field\":{s.Total.Id},\"op\":\"gt\",\"value\":0}}]}}"), [$"field:{titleId}"], null,
            Text("合計必須大於 0"), true)]))).IsSuccess.Should().BeTrue();

        var zero = await Create(s, NewRow(s, 0, 10));
        Err(zero, s.ParentTitle).Should().Be("合計必須大於 0");

        Num(await MustCreate(s, NewRow(s, 1, 10)), s.Total).Should().Be(10m);
    }
}
