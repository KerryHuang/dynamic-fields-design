using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 兩段式單據鏈（來源單 → 目標單，各有明細）的測試資料定義。完全由前綴與標籤決定名稱，
/// 同一份程式可以組出「報價 → 訂單」，也可以組出「請購 → 採購」，被測程式碼對業務名詞一無所知。
/// </summary>
public abstract class ConversionTestBase : DesignerTestBase
{
    /// <param name="Src">來源表頭；SrcLine 是它的明細；Dst、DstLine 是目標。</param>
    protected sealed record Chain(
        string Src, string SrcLine, string Dst, string DstLine, string Party,
        long SrcMemo, long SrcParty, long SrcStatus, long SrcDraft, long SrcApproved,
        long LineItem, long LineQty, long LinePrice, long LineHead,
        long DstFrom, long DstMemo, long DstParty,
        long DstLineItem, long DstLineQty, long DstLinePrice, long DstLineFrom, long DstLineHead,
        long PartyName, long SrcTitle, long DstTitle, long LineTitle, long DstLineTitle);

    protected static string K(long id) => $"f_{id}";
    protected static string Opt(long id) => $"opt_{id}";

    /// <summary>建立四個模組與往來對象模組；來源有狀態機（草稿 → 已核准，已核准唯讀）。</summary>
    protected async Task<Chain> BuildChain(string p, string srcLabel = "來源單", string dstLabel = "目標單")
    {
        await NewModule($"{p}-party");
        await Send(new ActivateModuleRequest($"{p}-party"));
        var partyName = (await Detail($"{p}-party")).TitleFieldId!.Value;

        await NewModule($"{p}-src");
        var srcMemo = await MustAdd($"{p}-src", FieldType.Text, label: $"{srcLabel}備註");
        var srcParty = await MustAdd($"{p}-src", FieldType.Reference, RefTo($"{p}-party"), "往來對象");
        var status = await MustAdd($"{p}-src", FieldType.Enum, label: "狀態");
        var draft = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("草稿"), null))).Data!).Id;
        var approved = ((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("已核准"), null))).Data!).Id;

        await NewModule($"{p}-src-line");
        var head = await MustAdd($"{p}-src-line", FieldType.Reference, RefTo($"{p}-src", ReferenceRelation.Detail), "表頭");
        var item = await MustAdd($"{p}-src-line", FieldType.Text, label: "品項");
        var qty = await MustAdd($"{p}-src-line", FieldType.Decimal, label: "數量");
        var price = await MustAdd($"{p}-src-line", FieldType.Currency, label: "單價");
        await Send(new ActivateModuleRequest($"{p}-src-line"));

        await NewModule($"{p}-dst");
        var from = await MustAdd($"{p}-dst", FieldType.Reference, RefTo($"{p}-src") with { ReadOnly = true }, $"來源{srcLabel}");
        var dstMemo = await MustAdd($"{p}-dst", FieldType.Text, label: $"{dstLabel}備註");
        var dstParty = await MustAdd($"{p}-dst", FieldType.Reference, RefTo($"{p}-party"), "往來對象");

        await NewModule($"{p}-dst-line");
        var dstHead = await MustAdd($"{p}-dst-line", FieldType.Reference, RefTo($"{p}-dst", ReferenceRelation.Detail), "表頭");
        var dstItem = await MustAdd($"{p}-dst-line", FieldType.Text, label: "品項");
        var dstQty = await MustAdd($"{p}-dst-line", FieldType.Decimal, label: "數量");
        var dstPrice = await MustAdd($"{p}-dst-line", FieldType.Currency, label: "單價");
        var dstFromLine = await MustAdd($"{p}-dst-line", FieldType.Integer, new FieldConfig { ReadOnly = true }, "來源明細");
        await Send(new ActivateModuleRequest($"{p}-dst-line"));

        (await Send(new ActivateModuleRequest($"{p}-src"))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest($"{p}-dst"))).IsSuccess.Should().BeTrue();
        (await Send(new SaveWorkflowRequest($"{p}-src", new WorkflowInput(status.Id, draft,
            [new WorkflowState(draft), new WorkflowState(approved, true)], [new WorkflowTransition(draft, approved, [])])))).IsSuccess.Should().BeTrue();

        return new Chain($"{p}-src", $"{p}-src-line", $"{p}-dst", $"{p}-dst-line", $"{p}-party",
            srcMemo.Id, srcParty.Id, status.Id, draft, approved,
            item.Id, qty.Id, price.Id, head.Id,
            from.Id, dstMemo.Id, dstParty.Id,
            dstItem.Id, dstQty.Id, dstPrice.Id, dstFromLine.Id, dstHead.Id,
            partyName, (await Detail($"{p}-src")).TitleFieldId!.Value, (await Detail($"{p}-dst")).TitleFieldId!.Value,
            (await Detail($"{p}-src-line")).TitleFieldId!.Value, (await Detail($"{p}-dst-line")).TitleFieldId!.Value);
    }

    protected static ConversionDefinition LinesDefinition(Chain c, List<long>? sourceStates = null) => new(
        sourceStates ?? [],
        new ConversionHeader(c.DstFrom, [new FillMapping(c.SrcTitle, c.DstTitle), new FillMapping(c.SrcMemo, c.DstMemo), new FillMapping(c.SrcParty, c.DstParty)]),
        new ConversionLines(c.SrcLine, c.DstLine, c.DstLineFrom, new FillMapping(c.LineQty, c.DstLineQty),
            [new FillMapping(c.LineTitle, c.DstLineTitle), new FillMapping(c.LineItem, c.DstLineItem), new FillMapping(c.LinePrice, c.DstLinePrice)]));

    protected async Task<ServiceResult> Define(Chain c, string key, ConversionDefinition? def = null, bool active = true) =>
        await Send(new CreateConversionRequest(new ConversionInput(key, Text(key), c.Src, c.Dst, active, def ?? LinesDefinition(c))));

    protected async Task DefineOk(Chain c, string key, ConversionDefinition? def = null)
    {
        var r = await Define(c, key, def);
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
    }

    /// <summary>建一張來源單與若干明細列（品項名稱, 數量, 單價），回傳來源單與各明細列的 Id。</summary>
    protected async Task<(DynamicRecordResponse Header, long[] Lines)> NewSource(Chain c, params (string Item, decimal Qty, decimal Price)[] lines)
    {
        var party = await Send(new CreateRecordRequest(c.Party, new Dictionary<string, JsonElement?> { [K(c.PartyName)] = J("\"往來甲\"") }));
        var partyId = ((DynamicRecordResponse)party.Data!).Id;
        var rows = lines.Select(l => new DetailRowInput(null, new Dictionary<string, JsonElement?>
        {
            [K(c.LineTitle)] = J($"\"{l.Item}\""), [K(c.LineItem)] = J($"\"{l.Item}\""),
            [K(c.LineQty)] = J(l.Qty.ToString()), [K(c.LinePrice)] = J(l.Price.ToString())
        }, null)).ToList();
        var r = await Send(new CreateRecordRequest(c.Src, new Dictionary<string, JsonElement?>
        {
            [K(c.SrcTitle)] = J("\"S-001\""), [K(c.SrcMemo)] = J("\"來源備註\""), [K(c.SrcParty)] = J(partyId.ToString())
        }, null, new Dictionary<string, List<DetailRowInput>> { [c.SrcLine] = rows }));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var header = (DynamicRecordResponse)r.Data!;
        var ids = header.Details![c.SrcLine].Select(x => x.Id).ToArray();
        return (header, ids);
    }

    protected async Task<DynamicRecordResponse> Approve(Chain c, DynamicRecordResponse header)
    {
        var r = await Send(new UpdateRecordRequest(header.Id,
            new Dictionary<string, JsonElement?> { [K(c.SrcStatus)] = J($"\"{Opt(c.SrcApproved)}\"") }, header.RowVersion));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }
}
