using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace LoadTest;

/// <summary>負載資料的三個模組：科目（account）、客戶（customer）、分錄（ledger）；經一般的設計器命令建立。</summary>
public sealed record SchemaInfo(
    string AccountTitle, string AccountKind, string CustomerTitle, string CustomerRegion, string LedgerTitle,
    string LedgerDate, string LedgerAccount, string LedgerCustomer, string LedgerDebit, string LedgerCredit,
    string LedgerStatus, string LedgerNo,
    long LedgerDateId, long LedgerAccountId, long LedgerDebitId, long LedgerStatusId,
    string[] AccountKinds, string[] Regions, string[] Statuses);

public static class Schema
{
    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    public static async Task<SchemaInfo> CreateAsync(IMediator m)
    {
        var (accTitle, accKind, accKinds) = await Module(m, "account", "科目", ("kind", FieldType.Enum, new FieldConfig { IsFilterable = true, IsSortable = true }, 5));
        var (cusTitle, cusRegion, regions) = await Module(m, "customer", "客戶", ("region", FieldType.Enum, new FieldConfig { IsFilterable = true, IsSortable = true }, 8));

        await Send(m, new CreateModuleRequest("ledger", Text("分錄"), null, null));
        var mod = (ModuleDetailResponse)(await m.Send(new GetModuleRequest("ledger"))).Data!;
        var block = mod.Blocks[0].Id;
        var ledgerTitle = mod.Blocks[0].Fields.Single(f => f.Id == mod.TitleFieldId).Key;
        var filt = new FieldConfig { IsFilterable = true, IsSortable = true, ShowInList = true };

        async Task<FieldResponse> Add(FieldType t, string label, FieldConfig c) =>
            (FieldResponse)(await Send(m, new AddFieldRequest("ledger", block, t, Text(label), c))).Data!;

        var date = await Add(FieldType.Date, "日期", filt);
        var account = await Add(FieldType.Reference, "科目", filt with { IsSortable = false, RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "account") });
        var customer = await Add(FieldType.Reference, "客戶", filt with { IsSortable = false, RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "customer") });
        var debit = await Add(FieldType.Currency, "借方", filt with { Scale = 2 });
        var credit = await Add(FieldType.Currency, "貸方", filt with { Scale = 2 });
        var status = await Add(FieldType.Enum, "狀態", filt);
        var no = await Add(FieldType.AutoNumber, "編號", filt with { Format = "JV-{yyyyMM}-{00000}" });
        var statuses = new List<string>();
        foreach (var s in new[] { "已過帳", "草稿", "作廢" })
            statuses.Add(((OptionResponse)(await Send(m, new AddOptionRequest(status.Id, Text(s), null))).Data!).Value);
        await Send(m, new ActivateModuleRequest("ledger"));

        return new SchemaInfo(accTitle, accKind.Key, cusTitle, cusRegion.Key, ledgerTitle, date.Key, account.Key, customer.Key, debit.Key, credit.Key, status.Key, no.Key,
            date.Id, account.Id, debit.Id, status.Id, accKinds, regions, [.. statuses]);
    }

    private static async Task<(string TitleKey, FieldResponse Enum, string[] Options)> Module(
        IMediator m, string key, string name, (string Label, FieldType Type, FieldConfig Config, int Options) e)
    {
        await Send(m, new CreateModuleRequest(key, Text(name), null, null));
        var mod = (ModuleDetailResponse)(await m.Send(new GetModuleRequest(key))).Data!;
        var f = (FieldResponse)(await Send(m, new AddFieldRequest(key, mod.Blocks[0].Id, e.Type, Text(e.Label), e.Config))).Data!;
        var options = new List<string>();
        for (var i = 0; i < e.Options; i++)
            options.Add(((OptionResponse)(await Send(m, new AddOptionRequest(f.Id, Text($"{e.Label}{i}"), null))).Data!).Value);
        await Send(m, new ActivateModuleRequest(key));
        return (mod.Blocks[0].Fields.Single(x => x.Id == mod.TitleFieldId).Key, f, [.. options]);
    }

    private static async Task<DynamicModules.Application.Common.ServiceResult> Send(IMediator m, IRequest<DynamicModules.Application.Common.ServiceResult> r)
    {
        var res = await m.Send(r);
        if (!res.IsSuccess && !res.IsCreated) throw new InvalidOperationException($"{r.GetType().Name} 失敗：{res.Message} {string.Join("; ", res.Errors.Select(e => e.Key + "=" + string.Join(",", e.Value)))}");
        return res;
    }
}
