using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>事件周邊的守衛：後盾、靜默的彙總寫入、匯入、連動結果查詢。兩組情境：單據明細與餘額彙總。</summary>
public class EventGuardTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<ServiceResult> Run(IRequest<ServiceResult> request)
    {
        var result = await Send(request);
        if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();
        return result;
    }

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values)
    {
        var r = await Send(new CreateRecordRequest(module, values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    [Fact]
    public async Task A_set_null_cascade_cannot_change_a_booked_source_the_delete_is_refused_up_front()
    {
        var s = await BuildStock("egb");
        await NewModule("egb-cust", activate: true);
        var custRef = await MustAdd(s.Doc, FieldType.Reference, RefTo("egb-cust", onDelete: ReferenceOnDelete.SetNull), "客戶");
        (await Send(new CreateEventRequest(Event("egb-post", s.Doc, new EventTrigger("create", null, null, null),
            Create("post", s.Move, null, Const(s.MoveTitle, "\"出貨\""), Const(s.MoveItem, "\"A\""), Const(s.MoveQty, "1")))))).IsCreated.Should().BeTrue();

        var cust = await MustCreate("egb-cust", Vals(((await Detail("egb-cust")).TitleFieldId!.Value, "\"c1\"")));
        var doc = await MustCreate(s.Doc, Vals(((await Detail(s.Doc)).TitleFieldId!.Value, "\"d\""), (custRef.Id, cust.Id.ToString())));

        // 刪客戶 → 連帶會把單據的客戶欄位清空：單據已記帳，刪除在寫入前就被擋下（後盾的直接測試在 FactLockHardeningTests）
        var cust2 = (DynamicRecordResponse)(await Send(new DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(cust.Id))).Data!;
        var deleted = await Run(new DeleteRecordRequest(cust.Id, cust2.RowVersion));
        deleted.IsConflict.Should().BeTrue();
        deleted.Message.Should().Contain("已記帳");
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(r => r.Id == cust.Id).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Import_into_a_module_that_is_an_event_source_is_refused()
    {
        var s = await BuildStock("egi");
        (await Send(new CreateEventRequest(Event("egi-post", s.Doc, new EventTrigger("create", null, null, null),
            Create("x", s.Move, null, Const(s.MoveTitle, "\"x\"")))))).IsCreated.Should().BeTrue();
        var title = (await Detail(s.Doc)).TitleFieldId!.Value;
        var r = await Send(new ImportRecordsRequest(s.Doc, Encoding.UTF8.GetBytes($"f_{title}\r\na\r\n")));
        r.IsInvalid.Should().BeTrue();
        r.Errors["moduleKey"][0].Should().Contain("不支援匯入");
        // 沒有事件的模組照常可以匯入
        var plain = await NewModule("egi-plain", activate: true);
        var plainTitle = (await Detail(plain)).TitleFieldId!.Value;
        (await Send(new ImportRecordsRequest(plain, Encoding.UTF8.GetBytes($"f_{plainTitle}\r\na\r\n")))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Rollup_recalculation_is_silent_and_does_not_fire_update_events()
    {
        await NewModule("egr-parent", activate: false);
        var tally = await MustAdd("egr-parent", FieldType.Text, label: "備註");
        await NewModule("egr-child");
        var parentRef = await MustAdd("egr-child", FieldType.Reference, RefTo("egr-parent"), "父");
        var amount = await MustAdd("egr-child", FieldType.Decimal, label: "金額");
        await Send(new ActivateModuleRequest("egr-child"));
        var total = await MustAdd("egr-parent", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentRef.Id, RollupFunction.Sum, amount.Id, null) }, "合計");
        await Send(new ActivateModuleRequest("egr-parent"));
        await NewModule("egr-log", activate: true);
        var logTitle = (await Detail("egr-log")).TitleFieldId!.Value;

        (await Send(new CreateEventRequest(Event("egr-watch", "egr-parent", new EventTrigger("update", null, null, null),
            Create("log", "egr-log", null, Const(logTitle, "\"changed\"")))))).IsCreated.Should().BeTrue();

        var parent = await MustCreate("egr-parent", Vals(((await Detail("egr-parent")).TitleFieldId!.Value, "\"p\"")));
        await MustCreate("egr-child", Vals(((await Detail("egr-child")).TitleFieldId!.Value, "\"c\""), (parentRef.Id, parent.Id.ToString()), (amount.Id, "5")));

        // 子記錄新增讓父記錄的合計變了（彙總重算），但不觸發父記錄的 update 事件
        var parentNow = (DynamicRecordResponse)(await Send(new DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(parent.Id))).Data!;
        parentNow.Values[K(total.Id)]!.Value.GetDecimal().Should().Be(5);
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == "egr-log").Id).Should().Be(0);

        // 使用者自己修改父記錄才觸發
        (await Send(new UpdateRecordRequest(parent.Id, Vals((tally.Id, "\"x\"")), parentNow.RowVersion))).IsSuccess.Should().BeTrue();
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == "egr-log").Id).Should().Be(1);
    }

    [Fact]
    public async Task Silent_mark_from_a_rollup_does_not_hide_a_later_event_write_to_the_same_parent()
    {
        // 新增子記錄 → 彙總重算父記錄（靜默）→ 子記錄的事件再改父記錄的備註 → 父記錄的 update 事件仍須觸發
        await NewModule("egs-parent", activate: false);
        var note = await MustAdd("egs-parent", FieldType.Text, label: "備註");
        await NewModule("egs-child");
        var parentRef = await MustAdd("egs-child", FieldType.Reference, RefTo("egs-parent"), "父");
        var amount = await MustAdd("egs-child", FieldType.Decimal, label: "金額");
        await Send(new ActivateModuleRequest("egs-child"));
        await MustAdd("egs-parent", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentRef.Id, RollupFunction.Sum, amount.Id, null) }, "合計");
        await Send(new ActivateModuleRequest("egs-parent"));
        await NewModule("egs-log", activate: true);
        var logTitle = (await Detail("egs-log")).TitleFieldId!.Value;

        (await Send(new CreateEventRequest(Event("egs-touch", "egs-child", new EventTrigger("create", null, null, null),
            UpdateVia("touch", "egs-parent", parentRef.Id, Const(note.Id, "\"x\"")))))).IsCreated.Should().BeTrue();
        (await Send(new CreateEventRequest(Event("egs-watch", "egs-parent", new EventTrigger("update", null, null, [note.Id]),
            Create("log", "egs-log", null, Const(logTitle, "\"changed\"")))))).IsCreated.Should().BeTrue();

        var parent = await MustCreate("egs-parent", Vals(((await Detail("egs-parent")).TitleFieldId!.Value, "\"p\"")));
        await MustCreate("egs-child", Vals(((await Detail("egs-child")).TitleFieldId!.Value, "\"c\""), (parentRef.Id, parent.Id.ToString()), (amount.Id, "5")));

        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == "egs-log").Id).Should().Be(1);
    }

    [Fact]
    public async Task Effects_query_lists_the_produced_records_and_counts_the_ones_the_user_cannot_see()
    {
        var s = await BuildStock("ege");
        (await Send(new CreateEventRequest(Event("ege-post", s.Doc, OnTransition(s.DocDone),
            Create("post", s.Move, null, Const(s.MoveTitle, "\"出貨\""), Const(s.MoveItem, "\"A\""), Const(s.MoveQty, "-1")))))).IsCreated.Should().BeTrue();
        var doc = await MustCreate(s.Doc, Vals(((await Detail(s.Doc)).TitleFieldId!.Value, "\"d\"")));
        (await Send(new UpdateRecordRequest(doc.Id, Vals((s.DocStatus, $"\"opt_{s.DocDone}\"")), doc.RowVersion))).IsSuccess.Should().BeTrue();

        var effects = (RecordEffectsResponse)(await Send(new GetRecordEffectsRequest(doc.Id))).Data!;
        effects.HiddenCount.Should().Be(0);
        var item = effects.Items.Should().ContainSingle().Subject;
        (item.EventKey, item.EffectCode, item.Kind, item.TargetModuleKey, item.Title).Should().Be(("ege-post", "post", "create", s.Move, "出貨"));
        // 目標模組名稱帶整份多語系文字，由前端依介面語系挑（不固定繁中）
        item.TargetModuleName.Resolve("zh-TW").Should().NotBeEmpty();

        (await Send(new GetRecordEffectsRequest(987654))).IsNotFound.Should().BeTrue();
    }
}
