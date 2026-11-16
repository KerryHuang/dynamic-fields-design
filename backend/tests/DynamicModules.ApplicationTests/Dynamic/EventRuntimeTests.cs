using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 事件的執行期行為（設計 §4）。兩組不相干情境：出貨單轉已出貨 → 每列明細產生一筆庫存異動；
/// 請假單核准 → 更新參照的餘額記錄。事件、檢查、分錄的名稱都只在測試的定義裡。
/// </summary>
public class EventRuntimeTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<ServiceResult> Run(IRequest<ServiceResult> request)
    {
        var result = await Send(request);
        if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();   // 測試共用一個 DbContext；正式環境每個請求自己一個 scope
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

    private int CountOf(string module) =>
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => !r.DeleteFlag && r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == module).Id);

    // ---- 出貨味 ----

    private sealed record Shipping(Stock S, long Cancelled);

    /// <summary>單據：草稿 → 已完成 → 已作廢（已完成可退回草稿）。異動設為分錄（數量取負號）。</summary>
    private async Task<Shipping> BuildShipping(string p)
    {
        var s = await BuildStock(p);
        var cancelled = ((OptionResponse)(await Send(new AddOptionRequest(s.DocStatus, Text("已作廢"), null))).Data!).Id;
        (await Send(new SaveWorkflowRequest(s.Doc, new WorkflowInput(s.DocStatus, s.DocDraft,
            [new WorkflowState(s.DocDraft), new WorkflowState(s.DocDone), new WorkflowState(cancelled, true)],
            [new WorkflowTransition(s.DocDraft, s.DocDone, []), new WorkflowTransition(s.DocDone, cancelled, []), new WorkflowTransition(s.DocDone, s.DocDraft, [])]))))
            .IsSuccess.Should().BeTrue();
        (await Send(new SaveLedgerRequest(s.Move, new LedgerInput(s.MoveReversal, [s.MoveQty], [], false)))).IsSuccess.Should().BeTrue();
        return new Shipping(s, cancelled);
    }

    private async Task PostEvent(Stock s)
    {
        var r = await Send(new CreateEventRequest(Event(s.Doc + "-post", s.Doc, OnTransition(s.DocDone),
            Create("post", s.Move, s.Line,
                Const(s.MoveTitle, "\"出貨\""), FromLine(s.MoveItem, s.LineItem), Formula(s.MoveQty, "line", $"-{{{s.LineQty}}}"), FromHeader(s.MoveWhere, s.DocMemo)))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    private async Task<DynamicRecordResponse> NewDoc(Stock s, params (string Item, string Qty)[] lines)
    {
        var lineTitle = (await Detail(s.Line)).TitleFieldId!.Value;
        var rows = lines.Select(l => new DetailRowInput(null, Vals((lineTitle, "\"行\""), (s.LineItem, $"\"{l.Item}\""), (s.LineQty, l.Qty)), null)).ToList();
        return await MustCreate(s.Doc, Vals((((await Detail(s.Doc)).TitleFieldId!.Value), "\"d1\""), (s.DocMemo, "\"w1\"")),
            new Dictionary<string, List<DetailRowInput>> { [s.Line] = rows });
    }

    private Task<ServiceResult> Transition(Stock s, DynamicRecordResponse doc, long to) =>
        Run(new UpdateRecordRequest(doc.Id, Vals((s.DocStatus, $"\"opt_{to}\"")), doc.RowVersion));

    private IReadOnlyList<EventFact> Facts() => Db.Context.Set<EventFact>().AsNoTracking().OrderBy(f => f.Id).ToList();

    private IReadOnlyList<(string Item, decimal Qty)> Moves(Stock s)
    {
        var moduleId = Db.Context.Set<Module>().Single(m => m.Key == s.Move).Id;
        return Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Where(r => r.ModuleId == moduleId && !r.DeleteFlag).OrderBy(r => r.Id).AsEnumerable()
            .Select(r => RecordValueProcessor.Parse(r.ValuesJson))
            .Select(v => (v[K(s.MoveItem)]!.Value.GetString()!, v[K(s.MoveQty)]!.Value.GetDecimal())).ToList();
    }

    [Fact]
    public async Task Transition_creates_one_move_per_line_and_records_the_facts()
    {
        var ship = await BuildShipping("evr");
        var s = ship.S;
        await PostEvent(s);
        var doc = await NewDoc(s, ("A", "5"), ("B", "2"));
        CountOf(s.Move).Should().Be(0);   // 草稿不觸發

        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();

        Moves(s).Should().BeEquivalentTo(new[] { ("A", -5m), ("B", -2m) });
        var facts = Facts();
        facts.Should().HaveCount(2);
        facts.Should().OnlyContain(f => f.EventKey == s.Doc + "-post" && f.EffectCode == "post" && f.SourceRecordId == doc.Id && f.OperationId != "");
        facts.Select(f => f.ItemKey).Distinct().Should().HaveCount(2);
        facts.Select(f => f.OperationId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task Going_back_and_forth_does_not_post_twice()
    {
        var ship = await BuildShipping("evb");
        var s = ship.S;
        await PostEvent(s);
        var doc = await NewDoc(s, ("A", "5"));
        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();
        doc = await Reload(doc.Id);
        (await Transition(s, doc, s.DocDraft)).IsSuccess.Should().BeTrue();
        doc = await Reload(doc.Id);
        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();

        Moves(s).Should().HaveCount(1);
        Facts().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failing_effect_rolls_back_the_whole_request_including_the_source_change()
    {
        var ship = await BuildShipping("evf");
        var s = ship.S;
        await PostEvent(s);
        // 庫存不得為負：沒有進貨就出貨，異動寫入後被檢查擋下
        (await Send(new CreateCheckRequest(new CheckInput(s.Move + "-min", Text("min"), s.Move, true,
            new CheckDefinition([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null, new CheckAssert("gte", new CheckLimit(0, null, null)), Text("庫存不足")))))).IsCreated.Should().BeTrue();
        var doc = await NewDoc(s, ("A", "5"));

        var r = await Transition(s, doc, s.DocDone);
        r.IsInvalid.Should().BeTrue();
        r.Errors[RecordValueProcessor.RecordErrorKey][0].Should().Be("庫存不足");

        ((string)(await Reload(doc.Id)).Values[K(s.DocStatus)]!.Value.GetString()!).Should().Be($"opt_{s.DocDraft}");
        CountOf(s.Move).Should().Be(0);
        Facts().Should().BeEmpty();
    }

    [Fact]
    public async Task Stock_in_then_out_passes_the_check_and_a_second_out_is_blocked()
    {
        var ship = await BuildShipping("evg");
        var s = ship.S;
        await PostEvent(s);
        (await Send(new CreateCheckRequest(new CheckInput(s.Move + "-min", Text("min"), s.Move, true,
            new CheckDefinition([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null, new CheckAssert("gte", new CheckLimit(0, null, null)), Text("庫存不足")))))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Vals((s.MoveTitle, "\"進貨\""), (s.MoveItem, "\"A\""), (s.MoveQty, "7"), (s.MoveWhere, "\"w1\"")));

        var first = await NewDoc(s, ("A", "5"));
        (await Transition(s, first, s.DocDone)).IsSuccess.Should().BeTrue();
        var second = await NewDoc(s, ("A", "5"));
        (await Transition(s, second, s.DocDone)).IsInvalid.Should().BeTrue();
        Moves(s).Select(m => m.Qty).Sum().Should().Be(2);
    }

    [Fact]
    public async Task A_source_with_facts_is_frozen_except_for_its_status()
    {
        var ship = await BuildShipping("evz");
        var s = ship.S;
        await PostEvent(s);
        var doc = await NewDoc(s, ("A", "5"));
        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();
        doc = await Reload(doc.Id);

        var edit = await Run(new UpdateRecordRequest(doc.Id, Vals((s.DocMemo, "\"w2\"")), doc.RowVersion));
        edit.IsSuccess.Should().BeFalse();
        (await Run(new DeleteRecordRequest(doc.Id, doc.RowVersion))).IsConflict.Should().BeTrue();
        (await Reload(doc.Id)).FrozenByEffects.Should().BeTrue();

        // 狀態欄位照常可轉移
        (await Transition(s, doc, ship.Cancelled)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_reverses_the_ledger_moves_exactly_once()
    {
        var ship = await BuildShipping("evv");
        var s = ship.S;
        await PostEvent(s);
        (await Send(new CreateEventRequest(Event(s.Doc + "-undo", s.Doc, new EventTrigger("transition", null, ship.Cancelled, null),
            new EventEffect("undo", "reverse", s.Move, null, null, null, s.Doc + "-post", "post", null, null))))).IsCreated.Should().BeTrue();
        var doc = await NewDoc(s, ("A", "5"), ("B", "2"));
        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();
        doc = await Reload(doc.Id);

        (await Transition(s, doc, ship.Cancelled)).IsSuccess.Should().BeTrue();

        Moves(s).Should().BeEquivalentTo(new[] { ("A", -5m), ("B", -2m), ("A", 5m), ("B", 2m) });
        Db.Context.Set<LedgerReversal>().AsNoTracking().Count().Should().Be(2);
        Facts().Should().HaveCount(4);   // 2 筆新增 + 2 筆沖銷
    }

    // ---- 請假味 ----

    [Fact]
    public async Task Approval_updates_the_referenced_balance_and_an_empty_reference_is_skipped()
    {
        var l = await BuildLeave("evl");
        (await Send(new CreateEventRequest(Event("evl-touch", l.Req, OnTransition(l.ReqApproved),
            UpdateVia("touch", l.Balance, l.ReqBalance, Const(l.BalLast, "\"today\"")))))).IsCreated.Should().BeTrue();
        var balance = await MustCreate(l.Balance, Vals((l.BalName, "\"b1\""), (l.BalLimit, "5")));
        var title = (await Detail(l.Req)).TitleFieldId!.Value;
        var req = await MustCreate(l.Req, Vals((title, "\"r1\""), (l.ReqBalance, balance.Id.ToString()), (l.ReqDays, "2")));
        var orphan = await MustCreate(l.Req, Vals((title, "\"r2\""), (l.ReqDays, "1")));

        (await Run(new UpdateRecordRequest(req.Id, Vals((l.ReqStatus, $"\"opt_{l.ReqApproved}\"")), req.RowVersion))).IsSuccess.Should().BeTrue();
        (await Reload(balance.Id)).Values[K(l.BalLast)]!.Value.GetString().Should().Be("2026-11-04");

        // 沒有選餘額：略過，不算錯誤
        (await Run(new UpdateRecordRequest(orphan.Id, Vals((l.ReqStatus, $"\"opt_{l.ReqApproved}\"")), orphan.RowVersion))).IsSuccess.Should().BeTrue();
        Facts().Should().BeEmpty();   // update 效果不寫事實
    }

    // ---- 深度與循環 ----

    [Fact]
    public async Task A_chain_deeper_than_five_levels_fails_and_rolls_back()
    {
        var keys = new List<string>();
        var titles = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var k = $"evd-{i}";
            await NewModule(k);
            await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest(k));
            keys.Add(k);
            titles.Add((await Detail(k)).TitleFieldId!.Value);
        }
        for (var i = 0; i < 6; i++)
        {
            var r = await Send(new CreateEventRequest(Event($"evd-e{i}", keys[i], new EventTrigger("create", null, null, null),
                Create("next", keys[i + 1], null, Const(titles[i + 1], "\"x\"")))));
            r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        }

        var tooDeep = await Run(new CreateRecordRequest(keys[0], Vals((titles[0], "\"x\""))));
        tooDeep.IsConflict.Should().BeTrue();
        tooDeep.Message.Should().Contain("過深");
        foreach (var k in keys) CountOf(k).Should().Be(0);

        // 拿掉最後一個事件：深度剛好 5，成功
        (await Send(new DeleteEventRequest("evd-e5"))).IsSuccess.Should().BeTrue();
        (await Send(new CreateRecordRequest(keys[0], Vals((titles[0], "\"x\""))))).IsCreated.Should().BeTrue();
        foreach (var k in keys.Take(6)) CountOf(k).Should().Be(1);
        CountOf(keys[6]).Should().Be(0);
    }

    // ---- 擴充點 ----

    [Fact]
    public async Task A_custom_handler_runs_through_the_restricted_writer_and_cannot_write_undeclared_modules()
    {
        var s = await BuildStock("evc");
        await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest(s.Doc));
        var okEvent = Event("evc-ok", s.Doc, OnTransition(s.DocDone),
            new EventEffect("run", "custom", null, null, null, null, null, null, FakeWriterHandler.HandlerName,
                J($$"""{"module":"{{s.Move}}","title":{{s.MoveTitle}}}""")));
        (await Send(new CreateEventRequest(okEvent))).IsCreated.Should().BeTrue();
        var doc = await MustCreate(s.Doc, Vals((((await Detail(s.Doc)).TitleFieldId!.Value), "\"d\"")));
        (await Transition(s, doc, s.DocDone)).IsSuccess.Should().BeTrue();
        CountOf(s.Move).Should().Be(1);

        // 宣告寫 Move，執行時卻想寫 Doc：立刻失敗，整筆回滾
        (await Send(new UpdateEventRequest("evc-ok", okEvent with
        {
            Definition = okEvent.Definition with
            {
                Effects = [okEvent.Definition.Effects[0] with { Args = J($$"""{"module":"{{s.Move}}","title":{{s.MoveTitle}},"sneak":"{{s.Doc}}"}""") }]
            }
        }))).IsSuccess.Should().BeTrue();
        var doc2 = await MustCreate(s.Doc, Vals((((await Detail(s.Doc)).TitleFieldId!.Value), "\"d2\"")));
        var denied = await Transition(s, doc2, s.DocDone);
        denied.IsSuccess.Should().BeFalse();
        CountOf(s.Move).Should().Be(1);
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IEffectHandler, FakeWriterHandler>();

    // ---- 定義變更的整批重算不動已固定的記錄（設計 §10.4） ----

    private async Task<object?> FormulaOf(long id, long formulaId)
    {
        var values = (await Reload(id)).Values;
        return values.TryGetValue(K(formulaId), out var v) && v is { } e ? e.GetDecimal() : null;
    }

    [Fact]
    public async Task Adding_a_formula_skips_sources_that_already_have_facts_but_updates_drafts()
    {
        var ship = await BuildShipping("evh");
        var s = ship.S;
        await PostEvent(s);
        var draft = await NewDoc(s, ("A", "1"));
        var posted = await NewDoc(s, ("B", "1"));
        (await Transition(s, posted, s.DocDone)).IsSuccess.Should().BeTrue();   // 沒有唯讀狀態，但已有事實
        var version = (await Reload(posted.Id)).RowVersion;

        var formula = await MustAdd(s.Doc, FieldType.Formula, new FieldConfig { Formula = "7", ResultType = "number" }, "常數");

        (await FormulaOf(draft.Id, formula.Id)).Should().Be(7m);
        (await FormulaOf(posted.Id, formula.Id)).Should().BeNull();
        (await Reload(posted.Id)).RowVersion.Should().Be(version);
    }

    [Fact]
    public async Task Adding_a_formula_skips_records_in_a_readonly_workflow_state()
    {
        var s = await BuildStock("evi");   // 已完成為唯讀狀態，沒有任何事件
        var draft = await NewDoc(s, ("A", "1"));
        var done = await NewDoc(s, ("B", "1"));
        (await Run(new UpdateRecordRequest(done.Id, Vals((s.DocStatus, $"\"opt_{s.DocDone}\"")), done.RowVersion))).IsSuccess.Should().BeTrue();
        var version = (await Reload(done.Id)).RowVersion;

        var formula = await MustAdd(s.Doc, FieldType.Formula, new FieldConfig { Formula = "7", ResultType = "number" }, "常數");

        (await FormulaOf(draft.Id, formula.Id)).Should().Be(7m);
        (await FormulaOf(done.Id, formula.Id)).Should().BeNull();
        (await Reload(done.Id)).RowVersion.Should().Be(version);
    }
}

/// <summary>測試用的假外掛：args 指定要寫的模組與標題欄位；sneak 指定一個沒宣告的模組，驗證受限寫入器會擋。</summary>
public sealed class FakeWriterHandler : IEffectHandler
{
    public const string HandlerName = "fake-writer";
    public string Name => HandlerName;

    public IReadOnlyList<string> ValidateArgs(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("module", out _) ? [] : ["缺少 module"];

    public IReadOnlyList<EffectWrite> DeclareWrites(JsonElement? args) =>
        [new EffectWrite(args!.Value.GetProperty("module").GetString()!, Create: true)];

    public async Task<EffectResult> ApplyAsync(EffectContext context, CancellationToken ct)
    {
        var args = context.Args!.Value;
        var title = args.GetProperty("title").GetInt64();
        var created = await context.Writer.CreateAsync(args.GetProperty("module").GetString()!,
            new Dictionary<string, JsonElement?> { [$"f_{title}"] = JsonSerializer.SerializeToElement("by-plugin") }, ct);
        if (!created.IsSuccess) return EffectResult.Fail(created.Message ?? "寫入失敗");
        if (args.TryGetProperty("sneak", out var sneak))
        {
            var sneaked = await context.Writer.CreateAsync(sneak.GetString()!,
                new Dictionary<string, JsonElement?> { [$"f_{title}"] = JsonSerializer.SerializeToElement("nope") }, ct);
            if (!sneaked.IsSuccess) return EffectResult.Fail(sneaked.Message ?? "未宣告的寫入");
        }
        return EffectResult.Ok;
    }
}
