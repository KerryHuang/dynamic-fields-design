using System.Text.Json;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 事件、檢查、分錄測試共用的兩組不相干情境（定義都在測試裡，被測程式碼不知道業務名詞）：
/// Stock 是「單據 → 明細 → 異動」；Leave 是「申請單參照餘額記錄」。
/// </summary>
public abstract class EffectsTestBase : DesignerTestBase
{
    protected static string K(long id) => $"f_{id}";
    protected static string Opt(long id) => $"opt_{id}";

    protected sealed record Stock(
        string Doc, string Line, string Move,
        long DocStatus, long DocDraft, long DocDone, long DocMemo,
        long LineHead, long LineItem, long LineQty,
        long MoveItem, long MoveQty, long MoveWhere, long MoveSource, long MoveReversal, long MoveTitle);

    /// <summary>單據（草稿 → 已完成，有狀態機）、單據明細、異動（尚未設為分錄，也還沒啟用）。</summary>
    protected async Task<Stock> BuildStock(string p, bool activateMove = true)
    {
        await NewModule($"{p}-doc");
        var memo = await MustAdd($"{p}-doc", FieldType.Text, label: "備註");
        var status = await MustAdd($"{p}-doc", FieldType.Enum, label: "狀態");
        var draft = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("草稿"), null))).Data!).Id;
        var done = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("已完成"), null))).Data!).Id;

        await NewModule($"{p}-line");
        var head = await MustAdd($"{p}-line", FieldType.Reference, RefTo($"{p}-doc", ReferenceRelation.Detail), "表頭");
        var item = await MustAdd($"{p}-line", FieldType.Text, label: "品項");
        var qty = await MustAdd($"{p}-line", FieldType.Decimal, label: "數量");
        await Send(new ActivateModuleRequest($"{p}-line"));

        await NewModule($"{p}-move");
        var mItem = await MustAdd($"{p}-move", FieldType.Text, new FieldConfig { IsFilterable = true }, "品項");
        var mQty = await MustAdd($"{p}-move", FieldType.Decimal, new FieldConfig { IsFilterable = true }, "數量");
        var mWhere = await MustAdd($"{p}-move", FieldType.Text, new FieldConfig { IsFilterable = true }, "倉庫");
        var mSource = await MustAdd($"{p}-move", FieldType.Reference, RefTo($"{p}-doc") with { ReadOnly = true }, "來源單據");
        var mReversal = await MustAdd($"{p}-move", FieldType.Reference, RefTo($"{p}-move") with { ReadOnly = true }, "沖銷自");
        if (activateMove) (await Send(new ActivateModuleRequest($"{p}-move"))).IsSuccess.Should().BeTrue();

        (await Send(new ActivateModuleRequest($"{p}-doc"))).IsSuccess.Should().BeTrue();
        (await Send(new SaveWorkflowRequest($"{p}-doc", new WorkflowInput(status.Id, draft,
            [new WorkflowState(draft), new WorkflowState(done, true)], [new WorkflowTransition(draft, done, [])])))).IsSuccess.Should().BeTrue();

        return new Stock($"{p}-doc", $"{p}-line", $"{p}-move", status.Id, draft, done, memo.Id, head.Id, item.Id, qty.Id,
            mItem.Id, mQty.Id, mWhere.Id, mSource.Id, mReversal.Id, (await Detail($"{p}-move")).TitleFieldId!.Value);
    }

    protected sealed record Leave(
        string Req, string Balance,
        long ReqStatus, long ReqDraft, long ReqApproved, long ReqBalance, long ReqDays,
        long BalLimit, long BalName, long BalLast);

    /// <summary>申請單（草稿 → 已核准，有狀態機）參照餘額記錄；餘額有額度與「最後核准日」。</summary>
    protected async Task<Leave> BuildLeave(string p)
    {
        await NewModule($"{p}-balance");
        var limit = await MustAdd($"{p}-balance", FieldType.Decimal, new FieldConfig { IsFilterable = true }, "額度");
        var last = await MustAdd($"{p}-balance", FieldType.Date, label: "最後核准日");
        await Send(new ActivateModuleRequest($"{p}-balance"));

        await NewModule($"{p}-req");
        var bal = await MustAdd($"{p}-req", FieldType.Reference, RefTo($"{p}-balance"), "餘額");
        var days = await MustAdd($"{p}-req", FieldType.Decimal, new FieldConfig { IsFilterable = true }, "天數");
        var status = await MustAdd($"{p}-req", FieldType.Enum, label: "狀態");
        var draft = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("草稿"), null))).Data!).Id;
        var approved = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("已核准"), null))).Data!).Id;
        (await Send(new ActivateModuleRequest($"{p}-req"))).IsSuccess.Should().BeTrue();
        (await Send(new SaveWorkflowRequest($"{p}-req", new WorkflowInput(status.Id, draft,
            [new WorkflowState(draft), new WorkflowState(approved, true)], [new WorkflowTransition(draft, approved, [])])))).IsSuccess.Should().BeTrue();
        return new Leave($"{p}-req", $"{p}-balance", status.Id, draft, approved, bal.Id, days.Id, limit.Id,
            (await Detail($"{p}-balance")).TitleFieldId!.Value, last.Id);
    }

    protected static EventInput Event(string key, string source, EventTrigger trigger, params EventEffect[] effects) =>
        new(key, Text(key), source, true, new EventDefinition(trigger, null, [.. effects]));

    protected static EventTrigger OnTransition(long to) => new("transition", null, to, null);

    protected static EventEffect Create(string code, string target, string? each, params EventSet[] set) =>
        new(code, "create", target, each, null, [.. set], null, null, null, null);

    protected static EventEffect UpdateVia(string code, string target, long via, params EventSet[] set) =>
        new(code, "update", target, null, via, [.. set], null, null, null, null);

    protected static EventSet FromHeader(long to, long fieldId) => new(to, new EventFrom("header", fieldId), null, null, false);
    protected static EventSet FromLine(long to, long fieldId) => new(to, new EventFrom("line", fieldId), null, null, false);
    protected static EventSet Formula(long to, string side, string expr) => new(to, null, new EventFormula(side, expr), null, false);
    protected static EventSet Const(long to, string json) => new(to, null, null, JsonDocument.Parse(json).RootElement.Clone(), false);

    protected long AnyGroupId() => Db.Context.Set<PermissionGroup>().AsNoTracking().OrderBy(g => g.Id).First().Id;
}
