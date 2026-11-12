using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 調查紀錄（a2d5 修正）：既有的 ImmutableDataGuard 在 DeleteField／DeleteBlock 同樣掛在「Purge 非零」閘門後面。
/// 公式與彙總沒有歷程與索引列，分錄模組已有記錄時刪除已停用的公式，守衛不跑、記錄上的值被清掉。
/// 這是既有行為，a2d5 不修；這條測試只釘住目前行為，修掉時預期會變紅，把斷言改成 409 即可。
/// </summary>
public class ImmutableDataGuardGapTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    [Fact]
    public async Task DeleteFormulaWithValue_LedgerModuleHasRecords_CurrentlySucceedsAndClearsValue()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module, r.Extra);
        var derived = await MustAdd(r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{r.Discount}}} + 1", ResultType = "number", Scale = 0 }, "折扣加一");
        As("buyer1");
        var po = await NewPo(r, extra: V((r.Discount, 100)));
        As("admin");
        Db.Context.Add(new ModuleLedger { ModuleId = r.ModuleId, Definition = new LedgerDefinition() });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == po.Id)).ValuesJson.Should().Contain($"\"f_{derived.Id}\"");

        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteFieldRequest(derived.Id, 0));

        res.IsSuccess.Should().BeTrue(Dump(res));   // 目前沒有擋（漏洞）
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == po.Id)).ValuesJson.Should().NotContain($"\"f_{derived.Id}\"");
    }
}
