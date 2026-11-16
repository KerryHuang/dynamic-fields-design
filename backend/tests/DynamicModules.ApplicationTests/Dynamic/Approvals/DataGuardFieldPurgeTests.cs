using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 刪除欄位時，分錄（ImmutableDataGuard）與結案（ClosedDataGuard）守衛不能只看 Purge 計數：
/// 公式與彙總沒有歷程與索引列，Purge 全 0，但記錄上有值，刪除一樣會清掉受保護的資料。空欄位仍可刪。
/// </summary>
public class DataGuardFieldPurgeTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<(PurchaseRig R, FieldResponse Derived, DynamicRecordResponse Po)> PoWithFormulaValue()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module, r.Extra);
        var derived = await MustAdd(r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{r.Discount}}} + 1", ResultType = "number", Scale = 0 }, "折扣加一");
        As("buyer1");
        var po = await NewPo(r, extra: V((r.Discount, 100)));
        As("admin");
        return (r, derived, po);
    }

    /// <summary>公式引用一個沒填的欄位，所有記錄的公式結果都是 null（沒有值可清）。</summary>
    private async Task<(PurchaseRig R, FieldResponse Derived, DynamicRecordResponse Po)> PoWithNullFormula()
    {
        var r = await BuildPurchase();
        await GrantEveryone(r.Module, r.Extra);
        var blank = await MustAdd(r.Module, FieldType.Decimal, new FieldConfig { Scale = 0 }, "空值");
        var derived = await MustAdd(r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{blank.Id}}} + 1", ResultType = "number", Scale = 0 }, "空值加一");
        As("buyer1");
        var po = await NewPo(r);
        As("admin");
        using var doc = JsonDocument.Parse(await ValuesOf(po.Id));
        (!doc.RootElement.TryGetProperty($"f_{derived.Id}", out var v) || v.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("前提：公式結果是 null");
        return (r, derived, po);
    }

    private async Task<string> ValuesOf(long id)
    {
        Db.Context.ChangeTracker.Clear();
        return (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == id)).ValuesJson;
    }

    [Fact]
    public async Task DeleteFormulaWithValue_LedgerModuleHasRecords_Returns409_ValueKept()
    {
        var (r, derived, po) = await PoWithFormulaValue();
        Db.Context.Add(new ModuleLedger { ModuleId = r.ModuleId, Definition = new LedgerDefinition() });
        await Db.Context.SaveChangesAsync();
        (await ValuesOf(po.Id)).Should().Contain($"\"f_{derived.Id}\"");

        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteFieldRequest(derived.Id, 0));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageText.Get(null, MessageText.LedgerSchemaImmutable));   // 是分錄守衛擋的
        (await ValuesOf(po.Id)).Should().Contain($"\"f_{derived.Id}\"");
    }

    [Fact]
    public async Task DeleteFormulaWithValue_ClosedModule_Returns409_ValueKept()
    {
        var (r, derived, po) = await PoWithFormulaValue();
        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var res = await Send(new DeleteFieldRequest(derived.Id, 0));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageText.Get(null, MessageText.AppClosedNoRewrite));   // 是結案守衛擋的
        (await ValuesOf(po.Id)).Should().Contain($"\"f_{derived.Id}\"");
    }

    [Fact]
    public async Task DeleteFormulaAllNull_LedgerModuleHasRecords_Succeeds()
    {
        var (r, derived, _) = await PoWithNullFormula();
        Db.Context.Add(new ModuleLedger { ModuleId = r.ModuleId, Definition = new LedgerDefinition() });
        await Db.Context.SaveChangesAsync();
        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();

        var res = await Send(new DeleteFieldRequest(derived.Id, 0));

        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task DeleteFormulaAllNull_ClosedModule_Succeeds()
    {
        var (r, derived, _) = await PoWithNullFormula();
        (await Deactivate(derived.Id)).IsSuccess.Should().BeTrue();
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var res = await Send(new DeleteFieldRequest(derived.Id, 0));

        res.IsSuccess.Should().BeTrue(Dump(res));
    }
}
