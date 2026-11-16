using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
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

/// <summary>一般參照的彙總重算（決策 8）：req（維修申請：紀錄筆數、總工時、平均工時）被 rec（維修紀錄：申請、工時、備註）參照。</summary>
public class RollupRecalcTests : DesignerTestBase
{
    private sealed record S(
        string Req, string Rec, string ReqTitle, string RecTitle,
        FieldResponse Source, FieldResponse Hours, FieldResponse Note,
        FieldResponse Count, FieldResponse Total, FieldResponse Avg);

    private async Task<S> Arrange()
    {
        var req = await NewModule("req");
        var rec = await NewModule("rec");
        var source = await MustAdd(rec, FieldType.Reference, RefTo(req, onDelete: ReferenceOnDelete.Cascade), "申請");
        var hours = await MustAdd(rec, FieldType.Decimal, new FieldConfig { Scale = 2 }, "工時");
        var note = await MustAdd(rec, FieldType.Text, new FieldConfig(), "備註");
        var count = await MustAdd(req, FieldType.Rollup, Roll(source.Id, RollupFunction.Count), "紀錄筆數");
        var total = await MustAdd(req, FieldType.Rollup, Roll(source.Id, RollupFunction.Sum, hours.Id), "總工時");
        var avg = await MustAdd(req, FieldType.Formula,
            new FieldConfig { Formula = $"{{{total.Id}}} / {{{count.Id}}}", ResultType = "number", Scale = 2 }, "平均工時");
        (await Send(new ActivateModuleRequest(req))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(rec))).IsSuccess.Should().BeTrue();
        return new S(req, rec, await TitleKey(req), await TitleKey(rec), source, hours, note, count, total, avg);
    }

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    private static FieldConfig Roll(long source, RollupFunction fn, long? aggregate = null, string? condition = null) =>
        new() { Rollup = new RollupConfig(source, fn, aggregate, condition is null ? null : J(condition)) };

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private static DynamicRecordResponse Ok(ServiceResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> New(string module, params (string Key, object? Value)[] values) =>
        Ok(await Send(new CreateRecordRequest(module, Vals(values))));

    private Task<DynamicRecordResponse> NewReq(S s, string title = "申請") => New(s.Req, (s.ReqTitle, title));

    private Task<DynamicRecordResponse> NewRec(S s, long reqId, decimal hours, string title = "紀錄") =>
        New(s.Rec, (s.RecTitle, title), (s.Source.Key, reqId), (s.Hours.Key, hours));

    private async Task<DynamicRecordResponse> Edit(DynamicRecordResponse r, params (string Key, object? Value)[] values) =>
        Ok(await Send(new UpdateRecordRequest(r.Id, Vals(values), (await Load(r.Id)).RowVersion)));

    private async Task Delete(long id) => Ok2(await Send(new DeleteRecordRequest(id, (await Load(id)).RowVersion)));

    private static void Ok2(ServiceResult r) =>
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));

    /// <summary>直接讀資料庫（不追蹤），看到的是已提交的結果。</summary>
    private Task<Record> Load(long id) => Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == id);

    private async Task<decimal?> Num(long id, FieldResponse f)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((await Load(id)).ValuesJson)!;
        return values.TryGetValue(f.Key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    }

    // ---- 新增 ----

    [Fact]
    public async Task Create_Child_ParentRollupUpdated()
    {
        var s = await Arrange();
        var a = await NewReq(s);

        await NewRec(s, a.Id, 1.5m);
        await NewRec(s, a.Id, 2.5m);

        (await Num(a.Id, s.Count)).Should().Be(2m);
        (await Num(a.Id, s.Total)).Should().Be(4m);
    }

    [Fact]
    public async Task Create_NewParent_CountSumZero()
    {
        var s = await Arrange();
        var max = await MustAdd(s.Req, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Max, s.Hours.Id), "最長工時");

        var a = await NewReq(s);

        (await Num(a.Id, s.Count)).Should().Be(0m);
        (await Num(a.Id, s.Total)).Should().Be(0m);
        (await Num(a.Id, max)).Should().BeNull();
    }

    [Fact]
    public async Task Create_Child_ParentRowVersionChanges()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 3m);

        (await Load(a.Id)).RowVersion.Should().NotBe(a.RowVersion);
        var stale = await Send(new UpdateRecordRequest(a.Id, Vals((s.ReqTitle, "改名")), a.RowVersion));
        stale.IsConflict.Should().BeTrue();
    }

    // ---- 修改 ----

    [Fact]
    public async Task Update_ChildHours_ParentSumUpdated()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        var r1 = await NewRec(s, a.Id, 1m);
        await NewRec(s, a.Id, 2m);

        await Edit(r1, (s.Hours.Key, 5m));

        (await Num(a.Id, s.Total)).Should().Be(7m);
    }

    [Fact]
    public async Task Update_ChildIrrelevantField_ParentRowVersionUnchanged()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        var r1 = await NewRec(s, a.Id, 1m);
        var before = await Load(a.Id);

        await Edit(r1, (s.Note.Key, "只改備註"));

        var after = await Load(a.Id);
        after.RowVersion.Should().Be(before.RowVersion);
        after.ValuesJson.Should().Be(before.ValuesJson);
    }

    [Fact]
    public async Task Update_ChildRefChanged_BothParentsRecalculated()
    {
        var s = await Arrange();
        var a = await NewReq(s, "A");
        var b = await NewReq(s, "B");
        var r1 = await NewRec(s, a.Id, 4m);
        await NewRec(s, a.Id, 1m);
        await NewRec(s, b.Id, 10m);

        await Edit(r1, (s.Source.Key, b.Id));

        (await Num(a.Id, s.Total)).Should().Be(1m);
        (await Num(a.Id, s.Count)).Should().Be(1m);
        (await Num(b.Id, s.Total)).Should().Be(14m);
        (await Num(b.Id, s.Count)).Should().Be(2m);
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_Child_ParentRollupUpdated()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        var r1 = await NewRec(s, a.Id, 4m);
        await NewRec(s, a.Id, 1m);

        await Delete(r1.Id);

        (await Num(a.Id, s.Total)).Should().Be(1m);
        (await Num(a.Id, s.Count)).Should().Be(1m);
    }

    [Fact]
    public async Task Delete_CascadeChild_ParentRollupRecalculated()
    {
        var s = await Arrange();
        var dev = await NewModule("dev", activate: true);
        var device = await MustAdd(s.Rec, FieldType.Reference, RefTo(dev, onDelete: ReferenceOnDelete.Cascade), "設備");
        var d = await New(dev, (await TitleKey(dev), "設備一"));
        var a = await NewReq(s);
        await New(s.Rec, (s.RecTitle, "r1"), (s.Source.Key, a.Id), (s.Hours.Key, 4m), (device.Key, d.Id));
        await NewRec(s, a.Id, 1m);

        await Delete(d.Id); // r1 因為指向設備而連帶刪除

        (await Num(a.Id, s.Total)).Should().Be(1m);
        (await Num(a.Id, s.Count)).Should().Be(1m);
    }

    [Fact]
    public async Task Delete_SetNullChild_ParentRollupRecalculated()
    {
        var s = await Arrange();
        var dev = await NewModule("dev", activate: true);
        var device = await MustAdd(s.Rec, FieldType.Reference, RefTo(dev, onDelete: ReferenceOnDelete.SetNull), "設備");
        var withDevice = await MustAdd(s.Req, FieldType.Rollup,
            Roll(s.Source.Id, RollupFunction.Count, condition: $$"""{"field":{{device.Id}},"op":"isNotEmpty"}"""), "有設備的筆數");
        var d = await New(dev, (await TitleKey(dev), "設備一"));
        var a = await NewReq(s);
        await New(s.Rec, (s.RecTitle, "r1"), (s.Source.Key, a.Id), (s.Hours.Key, 4m), (device.Key, d.Id));
        (await Num(a.Id, withDevice)).Should().Be(1m);

        await Delete(d.Id); // r1 的設備被清空，仍指向申請 A

        (await Num(a.Id, withDevice)).Should().Be(0m);
        (await Num(a.Id, s.Count)).Should().Be(1m);
    }

    [Fact]
    public async Task Delete_ParentWithChildren_NoRecalcOnDeletedParent()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        var r1 = await NewRec(s, a.Id, 4m);
        var beforeDelete = await Load(a.Id);

        await Delete(a.Id); // r1 連帶刪除；A 自己已刪，不再改寫

        var after = await Load(a.Id);
        after.DeleteFlag.Should().BeTrue();
        after.ValuesJson.Should().Be(beforeDelete.ValuesJson);
        (await Load(r1.Id)).DeleteFlag.Should().BeTrue();
    }

    // ---- 公式、權限 ----

    [Fact]
    public async Task ParentFormula_ReRunAfterRecalc()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 1m);
        await NewRec(s, a.Id, 2m);

        (await Num(a.Id, s.Avg)).Should().Be(1.5m);
    }

    [Fact]
    public async Task Rollup_IgnoresReaderScope()
    {
        var s = await Arrange();
        var floor = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "floor");
        foreach (var (key, scope) in new[] { (s.Req, DataScope.All), (s.Rec, DataScope.Own) })
        {
            var m = await Detail(key);
            Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = floor.Id, ProgramCode = m.ProgramCode!, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
            });
            Db.Context.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = floor.Id, Scope = scope });
        }
        await Db.Context.SaveChangesAsync();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 1m, "admin 的紀錄");

        As("floor01");
        await NewRec(s, a.Id, 2m, "floor01 的紀錄");

        (await Num(a.Id, s.Count)).Should().Be(2m);
        (await Num(a.Id, s.Total)).Should().Be(3m);
    }

    // ---- 設計器變更 ----

    [Fact]
    public async Task ChildFormulaChanged_ParentsRecalculated()
    {
        var s = await Arrange();
        var doubled = await MustAdd(s.Rec, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Hours.Id}}} * 2", ResultType = "number", Scale = 2 }, "加倍工時");
        var sumDoubled = await MustAdd(s.Req, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, doubled.Id), "加倍總工時");
        var a = await NewReq(s, "A");
        var b = await NewReq(s, "B");
        await NewRec(s, a.Id, 1m);
        await NewRec(s, b.Id, 2m);
        (await Num(a.Id, sumDoubled)).Should().Be(2m);

        Ok2(await Update(doubled, doubled.Config with { Formula = $"{{{s.Hours.Id}}} * 3" }));

        (await Num(a.Id, sumDoubled)).Should().Be(3m);
        (await Num(b.Id, sumDoubled)).Should().Be(6m);
    }

    [Fact]
    public async Task AddRollupField_BackfillsExistingParents()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 1m);
        await NewRec(s, a.Id, 5m);

        var max = await MustAdd(s.Req, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Max, s.Hours.Id), "最長工時");

        (await Num(a.Id, max)).Should().Be(5m);
    }

    [Fact]
    public async Task AggregateScaleChanged_RollupRederivedAndRecalculated()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 1.25m);
        await NewRec(s, a.Id, 1.25m);
        (await Num(a.Id, s.Total)).Should().Be(2.5m);

        // 工時改成整數位：總工時的 Scale 跟著推成 0，既有記錄重算後四捨五入
        Ok2(await Update(s.Hours, s.Hours.Config with { Scale = 0 }));
        (await Detail(s.Req)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == s.Total.Id).Config.Scale.Should().Be(0);
        (await Num(a.Id, s.Total)).Should().Be(3m);
    }

    [Fact]
    public async Task UpdateRollupLabelOnly_NoRecalc()
    {
        var s = await Arrange();
        var a = await NewReq(s);
        await NewRec(s, a.Id, 1m);
        // 故意把存好的筆數改錯：只有真的重算才會被修正，藉此觀察有沒有觸發重算
        var tracked = await Db.Context.Set<Record>().SingleAsync(r => r.Id == a.Id);
        tracked.ValuesJson = tracked.ValuesJson.Replace($"\"{s.Count.Key}\":1", $"\"{s.Count.Key}\":99");
        await Db.Context.SaveChangesAsync();
        (await Num(a.Id, s.Count)).Should().Be(99m);

        Ok2(await Send(new UpdateFieldRequest(s.Count.Id, s.Count.Type, Text("筆數（改名）"), s.Count.Config, s.Count.BlockId)));
        (await Num(a.Id, s.Count)).Should().Be(99m);

        var cond = J($$"""{"field":{{s.Hours.Id}},"op":"isNotEmpty"}""");
        Ok2(await Update(s.Count, s.Count.Config with { Rollup = s.Count.Config.Rollup! with { Condition = cond } }));
        (await Num(a.Id, s.Count)).Should().Be(1m);
    }

    [Fact]
    public async Task ReactivateRollupField_Backfills()
    {
        var s = await Arrange();
        var min = await MustAdd(s.Req, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Min, s.Hours.Id), "最短工時");
        var a = await NewReq(s);
        await NewRec(s, a.Id, 3m);
        (await Deactivate(min.Id)).IsSuccess.Should().BeTrue();
        await NewRec(s, a.Id, 1m);

        (await Send(new SetFieldStatusRequest(min.Id, FieldStatus.Active))).IsSuccess.Should().BeTrue();

        (await Num(a.Id, min)).Should().Be(1m);
    }

    // ---- 明細列、自我參照 ----

    /// <summary>order（表頭，參照 customer）＋ line（明細：產品、數量）；product 有「已訂數量」sum，customer 有「訂單數」count。</summary>
    private sealed record O(
        string Order, string Line, string OrderTitle, string LineTitle, FieldResponse ProductRef, FieldResponse Qty,
        FieldResponse CustomerRef, FieldResponse Sold, FieldResponse Orders, long P1, long P2, long Customer);

    private async Task<O> ArrangeOrders()
    {
        var product = await NewModule("product");
        var customer = await NewModule("customer");
        var order = await NewModule("order");
        var line = await NewModule("line");
        await MustAdd(line, FieldType.Reference, RefTo(order, ReferenceRelation.Detail), "表頭");
        var productRef = await MustAdd(line, FieldType.Reference, RefTo(product), "產品");
        var qty = await MustAdd(line, FieldType.Decimal, new FieldConfig { Scale = 2 }, "數量");
        var customerRef = await MustAdd(order, FieldType.Reference, RefTo(customer), "客戶");
        var sold = await MustAdd(product, FieldType.Rollup, Roll(productRef.Id, RollupFunction.Sum, qty.Id), "已訂數量");
        var orders = await MustAdd(customer, FieldType.Rollup, Roll(customerRef.Id, RollupFunction.Count), "訂單數");
        foreach (var m in new[] { product, customer, order, line })
            (await Send(new ActivateModuleRequest(m))).IsSuccess.Should().BeTrue();
        var p1 = await New(product, (await TitleKey(product), "螺絲"));
        var p2 = await New(product, (await TitleKey(product), "螺帽"));
        var c = await New(customer, (await TitleKey(customer), "客戶甲"));
        return new O(order, line, await TitleKey(order), await TitleKey(line), productRef, qty, customerRef, sold, orders, p1.Id, p2.Id, c.Id);
    }

    private static DetailRowInput Line(O o, long productId, decimal qty) =>
        new(null, Vals((o.LineTitle, "品項"), (o.ProductRef.Key, productId), (o.Qty.Key, qty)), null);

    private async Task<DynamicRecordResponse> NewOrder(O o, params DetailRowInput[] lines) =>
        Ok(await Send(new CreateRecordRequest(o.Order, Vals((o.OrderTitle, "訂單"), (o.CustomerRef.Key, o.Customer)), null,
            new() { [o.Line] = [.. lines] })));

    private async Task<DynamicRecordResponse> EditLines(O o, DynamicRecordResponse order, params DetailRowInput[] lines) =>
        Ok(await Send(new UpdateRecordRequest(order.Id, [], (await Load(order.Id)).RowVersion, new() { [o.Line] = [.. lines] })));

    [Fact]
    public async Task DetailRowLookup_RecalculatesOtherParent()
    {
        var o = await ArrangeOrders();

        await NewOrder(o, Line(o, o.P1, 3m), Line(o, o.P1, 4m));

        (await Num(o.P1, o.Sold)).Should().Be(7m);
    }

    [Fact]
    public async Task DetailRowLookupChanged_BothProductsRecalculated()
    {
        var o = await ArrangeOrders();
        var order = await NewOrder(o, Line(o, o.P1, 3m), Line(o, o.P1, 4m));
        var first = order.Details![o.Line][0];

        await EditLines(o, order, new DetailRowInput(first.Id, Vals((o.ProductRef.Key, o.P2)), first.RowVersion));

        (await Num(o.P1, o.Sold)).Should().Be(4m);
        (await Num(o.P2, o.Sold)).Should().Be(3m);
    }

    [Fact]
    public async Task DetailRowRemoved_ProductRecalculated()
    {
        var o = await ArrangeOrders();
        var order = await NewOrder(o, Line(o, o.P1, 3m), Line(o, o.P1, 4m));
        var first = order.Details![o.Line][0];

        await EditLines(o, order, new DetailRowInput(first.Id, null, first.RowVersion, true));

        (await Num(o.P1, o.Sold)).Should().Be(4m);
    }

    [Fact]
    public async Task HeaderLookup_RecalculatedOnCreateAndDelete()
    {
        var o = await ArrangeOrders();

        var order = await NewOrder(o, Line(o, o.P1, 3m));
        (await Num(o.Customer, o.Orders)).Should().Be(1m);

        await Delete(order.Id);
        (await Num(o.Customer, o.Orders)).Should().Be(0m);
        (await Num(o.P1, o.Sold)).Should().Be(0m);
    }

    [Fact]
    public async Task SelfReference_CountChildren()
    {
        var dev = await NewModule("dev");
        var upper = await MustAdd(dev, FieldType.Reference, RefTo(dev), "上層設備");
        var children = await MustAdd(dev, FieldType.Rollup, Roll(upper.Id, RollupFunction.Count), "子設備數");
        (await Send(new ActivateModuleRequest(dev))).IsSuccess.Should().BeTrue();
        var title = await TitleKey(dev);
        var top = await New(dev, (title, "主機"));

        await New(dev, (title, "馬達"), (upper.Key, top.Id));
        await New(dev, (title, "幫浦"), (upper.Key, top.Id));

        (await Num(top.Id, children)).Should().Be(2m);
    }
}
