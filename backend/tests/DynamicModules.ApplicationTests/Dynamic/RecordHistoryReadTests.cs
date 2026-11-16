using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 3、4：讀取歷程的範圍、權限過濾、顯示文字與分頁。</summary>
public class RecordHistoryReadTests : DesignerTestBase
{
    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private static DynamicRecordResponse Ok(ServiceResult r)
    {
        (r.IsSuccess || r.IsCreated).Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private Task<string> RowVersion(long id) =>
        Db.Context.Set<Record>().AsNoTracking().Where(r => r.Id == id).Select(r => r.RowVersion).SingleAsync();

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    private async Task<DynamicRecordResponse> Save(long id, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null) =>
        Ok(await Send(new UpdateRecordRequest(id, values, await RowVersion(id), details)));

    private async Task<PagedResponse<RecordHistoryResponse>> History(long id, int page = 1, int size = 20, string locale = "zh-TW")
    {
        var r = await Send(new GetRecordHistoryRequest(id, page, size, locale));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (PagedResponse<RecordHistoryResponse>)r.Data!;
    }

    private static void ShouldBeRecordNotFound(ServiceResult r)
    {
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("記錄不存在");
    }

    private async Task<long> SalesGroupId() =>
        (await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales")).Id;

    private async Task Grant(string module, bool pricing = true, DataScope? scope = null)
    {
        var m = await Detail(module);
        var sales = await SalesGroupId();
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales, ProgramCode = m.ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = pricing
        });
        if (scope is { } s) Db.Context.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = sales, Scope = s });
        await Db.Context.SaveChangesAsync();
    }

    private sealed record Order(string TitleKey, string LineTitleKey, FieldResponse Amount, FieldResponse Qty, FieldResponse Price);

    /// <summary>order（表頭，含金額）＋ line（明細，數量與單價），兩者都啟用。</summary>
    private async Task<Order> OrderWithLines()
    {
        await NewModule("order");
        await NewModule("line");
        await MustAdd("line", FieldType.Reference, RefTo("order", ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd("line", FieldType.Integer, label: "數量");
        var price = await MustAdd("line", FieldType.Currency, label: "單價");
        var amount = await MustAdd("order", FieldType.Currency, label: "金額");
        (await Send(new ActivateModuleRequest("order"))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest("line"))).IsSuccess.Should().BeTrue();
        return new Order(await TitleKey("order"), await TitleKey("line"), amount, qty, price);
    }

    [Fact]
    public async Task History_NewestFirst_WithDisplayText()
    {
        await NewModule("memo");
        var status = await MustAdd("memo", FieldType.Enum, label: "狀態");
        var a = (OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("草稿"), null))).Data!;
        var b = (OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("送審"), null))).Data!;
        var c = (OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("核准"), null))).Data!;
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        var title = await TitleKey("memo");
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (status.Key, a.Value)))));
        await Save(rec.Id, Vals((status.Key, b.Value)));
        await Save(rec.Id, Vals((status.Key, c.Value)));

        var page = await History(rec.Id);

        page.TotalCount.Should().Be(3);
        page.Items.Select(i => i.Operation).Should().Equal("update", "update", "create");
        var last = page.Items[0];
        last.FieldKey.Should().Be(status.Key);
        last.FieldLabel!.Resolve("zh-TW").Should().Be("狀態");
        last.OldText.Should().Be("送審");
        last.NewText.Should().Be("核准");
        last.ChangedUserId.Should().Be("admin");
        last.ChangedUserName.Should().Be("系統管理員");
        last.Detail.Should().BeNull();
        page.Items[1].OldText.Should().Be("草稿");
        page.Items[1].NewText.Should().Be("送審");
        page.Items[2].FieldKey.Should().BeNull();
        page.Items[2].OldText.Should().BeNull();
    }

    [Fact]
    public async Task History_IncludesDetailRows_WithRowTitle()
    {
        var o = await OrderWithLines();
        var header = Ok(await Send(new CreateRecordRequest("order", Vals((o.TitleKey, "訂單")), null, new()
        {
            ["line"] =
            [
                new DetailRowInput(null, Vals((o.LineTitleKey, "螺絲"), (o.Qty.Key, 1)), null, false),
                new DetailRowInput(null, Vals((o.LineTitleKey, "螺帽"), (o.Qty.Key, 2)), null, false)
            ]
        })));
        var (keep, drop) = (header.Details!["line"][0], header.Details!["line"][1]);
        await Save(header.Id, [], new()
        {
            ["line"] =
            [
                new DetailRowInput(keep.Id, Vals((o.Qty.Key, 5)), keep.RowVersion, false),
                new DetailRowInput(drop.Id, null, drop.RowVersion, true)
            ]
        });

        var items = (await History(header.Id)).Items;

        items.Should().HaveCount(5);
        items.Where(i => i.Detail is null).Select(i => i.Operation).Should().Equal("create");
        var update = items.Single(i => i.Operation == "update");
        update.Detail!.ModuleKey.Should().Be("line");
        update.Detail.ModuleName.Resolve("zh-TW").Should().Be("line");
        update.Detail.RecordId.Should().Be(keep.Id);
        update.Detail.Title.Should().Be("螺絲");
        update.FieldKey.Should().Be(o.Qty.Key);
        update.OldText.Should().Be("1");
        update.NewText.Should().Be("5");
        var deleted = items.Single(i => i.Operation == "delete");
        deleted.Detail!.RecordId.Should().Be(drop.Id);
        deleted.Detail.Title.Should().Be("螺帽");
        items.Count(i => i.Operation == "create" && i.Detail is not null).Should().Be(2);
    }

    [Fact]
    public async Task History_HiddenField_Omitted()
    {
        await NewModule("memo");
        var note = await MustAdd("memo", FieldType.Text, label: "備註");
        (await Send(new ActivateModuleRequest("memo"))).IsSuccess.Should().BeTrue();
        await Grant("memo");
        Db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = note.Id, GroupId = await SalesGroupId(), Access = FieldAccess.Hidden });
        await Db.Context.SaveChangesAsync();
        var title = await TitleKey("memo");
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a"), (note.Key, "x")))));
        await Save(rec.Id, Vals((title, "b"), (note.Key, "y")));

        As("sales01");
        var page = await History(rec.Id);

        page.TotalCount.Should().Be(2);
        page.Items.Select(i => (i.Operation, i.FieldKey)).Should().Equal(("update", title), ("create", null));
    }

    [Fact]
    public async Task History_CurrencyWithoutPricing_Omitted()
    {
        var o = await OrderWithLines();
        await Grant("order", pricing: false);
        var header = Ok(await Send(new CreateRecordRequest("order", Vals((o.TitleKey, "訂單"), (o.Amount.Key, 100)), null, new()
        {
            ["line"] = [new DetailRowInput(null, Vals((o.LineTitleKey, "螺絲"), (o.Qty.Key, 1), (o.Price.Key, 10)), null, false)]
        })));
        var line = header.Details!["line"][0];
        await Save(header.Id, Vals((o.Amount.Key, 200)), new()
        {
            ["line"] = [new DetailRowInput(line.Id, Vals((o.Qty.Key, 2), (o.Price.Key, 20)), line.RowVersion, false)]
        });

        As("sales01");
        var items = (await History(header.Id)).Items;

        items.Select(i => i.FieldKey).Should().NotContain([o.Amount.Key, o.Price.Key]);
        items.Should().ContainSingle(i => i.FieldKey == o.Qty.Key);
        items.Should().HaveCount(3); // 表頭 Create、明細 Create、數量 Update
    }

    [Fact]
    public async Task History_OutOfScope_404()
    {
        await NewModule("memo", activate: true);
        await Grant("memo", scope: DataScope.Own);
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((await TitleKey("memo"), "a")))));

        As("sales01");
        ShouldBeRecordNotFound(await Send(new GetRecordHistoryRequest(rec.Id, 1, 20, "zh-TW")));
    }

    [Fact]
    public async Task History_DetailRowId_404()
    {
        var o = await OrderWithLines();
        var header = Ok(await Send(new CreateRecordRequest("order", Vals((o.TitleKey, "訂單")), null, new()
        {
            ["line"] = [new DetailRowInput(null, Vals((o.LineTitleKey, "螺絲"), (o.Qty.Key, 1)), null, false)]
        })));

        ShouldBeRecordNotFound(await Send(new GetRecordHistoryRequest(header.Details!["line"][0].Id, 1, 20, "zh-TW")));
    }

    [Fact]
    public async Task History_ReferenceTitle_RespectsReaderAccess()
    {
        await NewModule("target", activate: true);
        await NewModule("source");
        var refField = await MustAdd("source", FieldType.Reference, RefTo("target"), "參照");
        (await Send(new ActivateModuleRequest("source"))).IsSuccess.Should().BeTrue();
        await Grant("source"); // sales 只有 source 的權限，沒有 target 的 Read
        var targetTitle = await TitleKey("target");
        var t1 = Ok(await Send(new CreateRecordRequest("target", Vals((targetTitle, "甲")))));
        var t2 = Ok(await Send(new CreateRecordRequest("target", Vals((targetTitle, "乙")))));
        var s = Ok(await Send(new CreateRecordRequest("source", Vals((await TitleKey("source"), "s"), (refField.Key, t1.Id)))));
        await Save(s.Id, Vals((refField.Key, t2.Id)));

        var byAdmin = (await History(s.Id)).Items[0];
        byAdmin.OldText.Should().Be("甲");
        byAdmin.NewText.Should().Be("乙");

        As("sales01");
        var bySales = (await History(s.Id)).Items[0];
        bySales.FieldKey.Should().Be(refField.Key);
        bySales.OldText.Should().Be("（無權限）");
        bySales.NewText.Should().Be("（無權限）");
    }

    [Fact]
    public async Task History_Paged()
    {
        await NewModule("memo", activate: true);
        var title = await TitleKey("memo");
        var rec = Ok(await Send(new CreateRecordRequest("memo", Vals((title, "a")))));
        await Save(rec.Id, Vals((title, "b")));
        await Save(rec.Id, Vals((title, "c")));
        await Save(rec.Id, Vals((title, "d")));
        await Save(rec.Id, Vals((title, "e")));

        var first = await History(rec.Id, page: 1, size: 2);
        var second = await History(rec.Id, page: 2, size: 2);
        var third = await History(rec.Id, page: 3, size: 2);

        first.TotalCount.Should().Be(5);
        first.CurrentPage.Should().Be(1);
        first.PagesSize.Should().Be(2);
        first.Items.Select(i => i.NewText).Should().Equal("e", "d");
        second.TotalCount.Should().Be(5);
        second.Items.Select(i => i.NewText).Should().Equal("c", "b");
        third.Items.Select(i => i.Operation).Should().Equal("create");
    }
}
