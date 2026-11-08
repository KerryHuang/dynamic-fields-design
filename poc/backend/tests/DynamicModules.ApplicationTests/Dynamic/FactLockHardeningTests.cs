using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>事實鎖的漏洞回歸（第一輪實作審查 a）：同 key 的他人請求、連帶刪除明細、明細公式重算、彙總連動公式。</summary>
public class FactLockHardeningTests : EffectsTestBase
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

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null)
    {
        var r = await Send(new CreateRecordRequest(module, values, Details: details));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Reload(long id) => (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    private IReadOnlyDictionary<string, JsonElement?> Stored(long id) =>
        RecordValueProcessor.Parse(Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(r => r.Id == id).ValuesJson);

    private async Task<long> TitleOf(string module) => (await Detail(module)).TitleFieldId!.Value;

    private int CountOf(string module) =>
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => !r.DeleteFlag && r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == module).Id);

    /// <summary>表頭（沒有狀態機）+ 明細（參照表頭、參照品項主檔且 Cascade）；表頭建立時每列明細各寫一筆日誌。</summary>
    private sealed record Booking(string Head, string Line, string Item, string Log, long LineItemRef, long LineQty, long LogTitle, long LogQty);

    private async Task<Booking> BuildBooking(string p, ReferenceOnDelete itemOnDelete, Func<Booking, EventSet[]>? extraSets = null)
    {
        await NewModule($"{p}-item", activate: true);
        await NewModule($"{p}-head");
        await Send(new ActivateModuleRequest($"{p}-head"));
        await NewModule($"{p}-line");
        await MustAdd($"{p}-line", FieldType.Reference, RefTo($"{p}-head", ReferenceRelation.Detail), "表頭");
        var itemRef = await MustAdd($"{p}-line", FieldType.Reference, RefTo($"{p}-item", onDelete: itemOnDelete), "品項");
        var qty = await MustAdd($"{p}-line", FieldType.Decimal, label: "數量");
        await Send(new ActivateModuleRequest($"{p}-line"));
        await NewModule($"{p}-log", activate: false);
        var logQty = await MustAdd($"{p}-log", FieldType.Decimal, label: "數量");
        await Send(new ActivateModuleRequest($"{p}-log"));
        var b = new Booking($"{p}-head", $"{p}-line", $"{p}-item", $"{p}-log", itemRef.Id, qty.Id, await TitleOf($"{p}-log"), logQty.Id);
        var sets = new List<EventSet> { Const(b.LogTitle, "\"x\""), FromLine(b.LogQty, b.LineQty) };
        if (extraSets is not null) sets.AddRange(extraSets(b));
        (await Send(new CreateEventRequest(Event($"{p}-post", b.Head, new EventTrigger("create", null, null, null),
            Create("post", b.Log, b.Line, [.. sets]))))).IsCreated.Should().BeTrue();
        return b;
    }

    private async Task<(DynamicRecordResponse Head, DynamicRecordResponse Item)> PostOneLine(Booking b, string qty, string? headKey = null)
    {
        var item = await MustCreate(b.Item, Vals((await TitleOf(b.Item), "\"i1\"")));
        var lineTitle = await TitleOf(b.Line);
        ClientKey = headKey;   // 只有建立表頭（觸發事件記帳）的那個請求帶 key
        var head = await MustCreate(b.Head, Vals((await TitleOf(b.Head), "\"h1\"")), new Dictionary<string, List<DetailRowInput>>
        {
            [b.Line] = [new DetailRowInput(null, Vals((lineTitle, "\"l\""), (b.LineItemRef, item.Id.ToString()), (b.LineQty, qty)), null)]
        });
        ClientKey = null;
        return (head, item);
    }

    [Fact]
    public async Task A_request_reusing_another_users_idempotency_key_cannot_edit_a_booked_source()
    {
        await NewModule("flk-master", activate: true);
        await NewModule("flk-src");
        var masterRef = await MustAdd("flk-src", FieldType.Reference, RefTo("flk-master", onDelete: ReferenceOnDelete.SetNull), "主檔");
        await Send(new ActivateModuleRequest("flk-src"));
        await NewModule("flk-log", activate: true);
        (await Send(new CreateEventRequest(Event("flk-post", "flk-src", new EventTrigger("create", null, null, null),
            Create("post", "flk-log", null, Const(await TitleOf("flk-log"), "\"x\"")))))).IsCreated.Should().BeTrue();
        foreach (var m in new[] { "flk-master", "flk-src" })   // 第二位使用者要有刪除、讀取權限，不然被擋的原因是權限而不是事實鎖
            (await Send(new SaveModulePermissionsRequest(m, new ModulePermissionsDto([],
                [new UserPermissionDto("sales01", new PrivilegesDto(true, true, true, true, false, false, false))], [], [])))).IsSuccess.Should().BeTrue();

        var masterTitle = await TitleOf("flk-master");
        var srcTitle = await TitleOf("flk-src");
        As("sales01");   // 主檔屬於第二位使用者（資料範圍只看自己的）
        var master = await MustCreate("flk-master", Vals((masterTitle, "\"m\"")));
        As("admin");
        ClientKey = "book0001";
        var src = await MustCreate("flk-src", Vals((srcTitle, "\"s\""), (masterRef.Id, master.Id.ToString())));

        As("sales01");
        var delete = await Run(new DeleteRecordRequest(master.Id, master.RowVersion));   // 同一把 key，SetNull 會清掉已記帳來源的參照

        delete.IsConflict.Should().BeTrue(delete.Message);
        Stored(src.Id).Should().ContainKey(K(masterRef.Id));
    }

    [Fact]
    public async Task Deleting_a_master_cannot_cascade_away_the_lines_of_a_booked_header()
    {
        var b = await BuildBooking("flc", ReferenceOnDelete.Cascade);
        var (head, item) = await PostOneLine(b, "5");
        CountOf(b.Log).Should().Be(1);

        var delete = await Run(new DeleteRecordRequest(item.Id, item.RowVersion));

        delete.IsConflict.Should().BeTrue();
        CountOf(b.Line).Should().Be(1);
        (await Reload(head.Id)).FrozenByEffects.Should().BeTrue();
    }

    [Fact]
    public async Task Changing_a_line_formula_does_not_recalculate_the_lines_of_a_booked_header()
    {
        var b = await BuildBooking("flf", ReferenceOnDelete.Restrict);
        var doubled = await MustAdd(b.Line, FieldType.Formula, new FieldConfig { Formula = $"{{{b.LineQty}}} * 2", ResultType = "number" }, "加倍");
        // 事件改成複製公式欄位值（記帳的是 qty×2）
        (await Send(new UpdateEventRequest("flf-post", Event("flf-post", b.Head, new EventTrigger("create", null, null, null),
            Create("post", b.Log, b.Line, Const(b.LogTitle, "\"x\""), FromLine(b.LogQty, doubled.Id)))))).IsSuccess.Should().BeTrue();
        var (_, _) = await PostOneLine(b, "5");

        var lineId = Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Single(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == b.Line).Id).Id;
        Stored(lineId)[K(doubled.Id)]!.Value.GetDecimal().Should().Be(10);

        (await Update(doubled, new FieldConfig { Formula = $"{{{b.LineQty}}} * 3", ResultType = "number" })).IsSuccess.Should().BeTrue();

        Stored(lineId)[K(doubled.Id)]!.Value.GetDecimal().Should().Be(10);   // 保留記帳當時的值
    }

    [Fact]
    public async Task A_rollup_change_does_not_recalculate_formulas_of_a_booked_source()
    {
        await NewModule("flr-parent");
        var rollupChild = "flr-child";
        await NewModule(rollupChild);
        var parentRef = await MustAdd(rollupChild, FieldType.Reference, RefTo("flr-parent"), "父");
        var amount = await MustAdd(rollupChild, FieldType.Decimal, label: "金額");
        await Send(new ActivateModuleRequest(rollupChild));
        var total = await MustAdd("flr-parent", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(parentRef.Id, RollupFunction.Sum, amount.Id, null) }, "合計");
        var doubled = await MustAdd("flr-parent", FieldType.Formula, new FieldConfig { Formula = $"{{{total.Id}}} * 2", ResultType = "number" }, "加倍");
        await Send(new ActivateModuleRequest("flr-parent"));
        await NewModule("flr-log", activate: true);
        (await Send(new CreateEventRequest(Event("flr-post", "flr-parent", new EventTrigger("create", null, null, null),
            Create("post", "flr-log", null, Const(await TitleOf("flr-log"), "\"x\"")))))).IsCreated.Should().BeTrue();
        var parent = await MustCreate("flr-parent", Vals((await TitleOf("flr-parent"), "\"p\"")));
        (await Reload(parent.Id)).Values[K(doubled.Id)]!.Value.GetDecimal().Should().Be(0);

        await MustCreate(rollupChild, Vals((await TitleOf(rollupChild), "\"c\""), (parentRef.Id, parent.Id.ToString()), (amount.Id, "5")));

        var after = await Reload(parent.Id);
        after.Values[K(total.Id)]!.Value.GetDecimal().Should().Be(5);      // 彙總照常更新
        after.Values[K(doubled.Id)]!.Value.GetDecimal().Should().Be(0);    // 公式保留記帳當時的值
    }

    // ---- 後盾本身（handler 前置檢查擋在前面時，後盾仍要獨立成立） ----

    private async Task<ServiceResult?> RunBackstop(string? clientKey, params (long RecordId, long ModuleId, string Before, string After, bool Deleted)[] changes)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IOperationContext>().ClientKey = clientKey;
        var journal = sp.GetRequiredService<IWriteJournal>();
        foreach (var c in changes)
            journal.Add(seq => new JournalEntry(seq, c.RecordId, c.ModuleId, JournalKind.Modified, c.Before, false, c.After, c.Deleted, false, "admin"));
        return await sp.GetRequiredService<FactBackstopStage>().RunAsync(CancellationToken.None);
    }

    private DynamicModules.Domain.Entities.Dynamic.Record Row(long id) =>
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(r => r.Id == id);

    [Fact]
    public async Task Backstop_does_not_treat_a_reused_idempotency_key_as_this_request()
    {
        var b = await BuildBooking("flb", ReferenceOnDelete.Restrict);
        var (head, _) = await PostOneLine(b, "5", headKey: "book0001");
        var row = Row(head.Id);
        var edited = RecordValueProcessor.Serialize(new Dictionary<string, JsonElement?>(RecordValueProcessor.Parse(row.ValuesJson)) { [K(await TitleOf(b.Head))] = J("\"changed\"") });

        // 另一個請求帶同一把 key：以前會被當成「本次請求才記的帳」而放行
        var result = await RunBackstop("book0001", (head.Id, row.ModuleId, row.ValuesJson, edited, false));

        result.Should().NotBeNull();
        result!.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Backstop_blocks_a_change_to_a_line_whose_header_is_booked()
    {
        var b = await BuildBooking("fll", ReferenceOnDelete.Restrict);
        var (head, _) = await PostOneLine(b, "5");
        var line = Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Single(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == b.Line).Id);
        var edited = RecordValueProcessor.Serialize(new Dictionary<string, JsonElement?>(RecordValueProcessor.Parse(line.ValuesJson)) { [K(b.LineQty)] = J("99") });

        (await RunBackstop(null, (line.Id, line.ModuleId, line.ValuesJson, edited, false)))!.IsConflict.Should().BeTrue();
        (await RunBackstop(null, (line.Id, line.ModuleId, line.ValuesJson, line.ValuesJson, true)))!.IsConflict.Should().BeTrue();   // 明細被刪
        (await RunBackstop(null, (line.Id, line.ModuleId, line.ValuesJson, line.ValuesJson, false))).Should().BeNull();               // 值沒變不擋
        head.Id.Should().BeGreaterThan(0);
    }
}
