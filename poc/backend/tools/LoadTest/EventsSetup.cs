using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LoadTest;

/// <summary>
/// 階段 2 的寫入負載情境：分錄新增時，連動事件在同一個交易內新增一筆鏡射記錄（標題、金額、科目），
/// 鏡射模組有一條「同科目金額加總不小於 0」的跨記錄檢查。已存在就略過，所以同一份資料庫可重複跑。
/// </summary>
public static class EventsSetup
{
    public const string Mirror = "mirror";

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    public static async Task EnsureAsync(IServiceProvider sp, SeedInfo seed)
    {
        using var scope = sp.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        if ((await m.Send(new GetModuleRequest(Mirror))).IsSuccess) return;
        var s = seed.Schema;

        await Send(m, new CreateModuleRequest(Mirror, Text("鏡射"), null, null));
        var mod = (ModuleDetailResponse)(await m.Send(new GetModuleRequest(Mirror))).Data!;
        var block = mod.Blocks[0].Id;
        var title = mod.Blocks[0].Fields.Single(f => f.Id == mod.TitleFieldId);
        var filt = new FieldConfig { IsFilterable = true };
        var amount = (FieldResponse)(await Send(m, new AddFieldRequest(Mirror, block, FieldType.Currency, Text("金額"), filt with { Scale = 2 }))).Data!;
        var account = (FieldResponse)(await Send(m, new AddFieldRequest(Mirror, block, FieldType.Reference, Text("科目"),
            filt with { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "account") }))).Data!;
        await Send(m, new ActivateModuleRequest(Mirror));

        var set = new List<EventSet>
        {
            new(title.Id, new EventFrom("header", long.Parse(s.LedgerTitle[2..])), null, null, false),
            new(amount.Id, new EventFrom("header", s.LedgerDebitId), null, null, false),
            new(account.Id, new EventFrom("header", s.LedgerAccountId), null, null, false),
        };
        var definition = new EventDefinition(new EventTrigger("create", null, null, null), null,
            [new EventEffect("mirror", "create", Mirror, null, null, set, null, null, null, null)]);
        await Send(m, new CreateEventRequest(new EventInput("lt-mirror", Text("鏡射"), "ledger", true, definition)));

        await Send(m, new CreateCheckRequest(new CheckInput("lt-mirror-min", Text("科目金額不小於零"), Mirror, true,
            new CheckDefinition([account.Id], "sum", amount.Id, null,
                new CheckAssert("gte", new CheckLimit(0, null, null)), Text("金額不足")))));
    }

    private static async Task<DynamicModules.Application.Common.ServiceResult> Send(IMediator m, IRequest<DynamicModules.Application.Common.ServiceResult> r)
    {
        var res = await m.Send(r);
        if (!res.IsSuccess && !res.IsCreated)
            throw new InvalidOperationException($"{r.GetType().Name} 失敗：{res.Message} {string.Join("; ", res.Errors.Select(e => e.Key + "=" + string.Join(",", e.Value)))}");
        return res;
    }
}
