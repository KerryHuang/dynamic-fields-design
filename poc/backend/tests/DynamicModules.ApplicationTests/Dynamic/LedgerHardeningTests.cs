using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>分錄沖銷的漏洞回歸（第一輪實作審查 c）：唯讀與不可見的沖銷欄位、單邊空值互換、明細互換。</summary>
public class LedgerHardeningTests : EffectsTestBase
{
    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<DynamicRecordResponse> Add(string module, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null)
    {
        var r = await Send(new CreateRecordRequest(module, values, Details: details));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private long SalesGroup() => Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales").Id;

    private static GroupPermissionDto ReadAdd(long group) =>
        new(group, new PrivilegesDto(true, true, false, false, false, false, false), DataScope.All);

    private int ReversalCount() => Db.Context.Set<LedgerReversal>().Count();

    [Fact]
    public async Task A_visible_but_readonly_negate_field_is_still_mirrored_by_the_reversal()
    {
        await NewModule("lhr");
        var rev = await MustAdd("lhr", FieldType.Reference, RefTo("lhr") with { ReadOnly = true }, "沖銷自");
        var qty = await MustAdd("lhr", FieldType.Decimal, new FieldConfig { DefaultValue = J("0") }, "數量");
        await Send(new ActivateModuleRequest("lhr"));
        (await Send(new SaveLedgerRequest("lhr", new LedgerInput(rev.Id, [qty.Id], [], false)))).IsSuccess.Should().BeTrue();
        var title = (await Detail("lhr")).TitleFieldId!.Value;
        var original = await Add("lhr", Vals((title, "\"x\""), (qty.Id, "10")));
        (await Send(new SaveModulePermissionsRequest("lhr", new ModulePermissionsDto(
            [ReadAdd(SalesGroup())], [], [new FieldPermissionDto(qty.Id, SalesGroup(), FieldAccess.ReadOnly)], [])))).IsSuccess.Should().BeTrue();

        As("sales01");
        var r = await Send(new ReverseRecordRequest(original.Id, null));

        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        ((DynamicRecordResponse)r.Data!).Values[K(qty.Id)]!.Value.GetDecimal().Should().Be(-10);   // 不是預設值 0
    }

    [Fact]
    public async Task A_detail_negate_field_the_user_cannot_see_is_refused_and_does_not_use_up_the_reversal()
    {
        await NewModule("lhd-head");
        var rev = await MustAdd("lhd-head", FieldType.Reference, RefTo("lhd-head") with { ReadOnly = true }, "沖銷自");
        await NewModule("lhd-line");
        await MustAdd("lhd-line", FieldType.Reference, RefTo("lhd-head", ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd("lhd-line", FieldType.Decimal, new FieldConfig { DefaultValue = J("0") }, "數量");
        await Send(new ActivateModuleRequest("lhd-line"));
        await Send(new ActivateModuleRequest("lhd-head"));
        (await Send(new SaveLedgerRequest("lhd-head", new LedgerInput(rev.Id, [qty.Id], [], false)))).IsSuccess.Should().BeTrue();
        var title = (await Detail("lhd-head")).TitleFieldId!.Value;
        var lineTitle = (await Detail("lhd-line")).TitleFieldId!.Value;
        var original = await Add("lhd-head", Vals((title, "\"x\"")),
            new() { ["lhd-line"] = [new DetailRowInput(null, Vals((lineTitle, "\"l\""), (qty.Id, "10")), null)] });
        (await Send(new SaveModulePermissionsRequest("lhd-head", new ModulePermissionsDto([ReadAdd(SalesGroup())], [], [], [])))).IsSuccess.Should().BeTrue();
        var hide = await Send(new SaveModulePermissionsRequest("lhd-line", new ModulePermissionsDto(
            [], [], [new FieldPermissionDto(qty.Id, SalesGroup(), FieldAccess.Hidden)], [])));   // 明細的群組權限沿用表頭，只能設欄位例外
        hide.IsSuccess.Should().BeTrue(hide.Message + string.Join(";", hide.Errors.Select(e => e.Key + e.Value[0])));

        As("sales01");
        var r = await Send(new ReverseRecordRequest(original.Id, null));

        r.IsInvalid.Should().BeTrue(r.Message);
        ReversalCount().Should().Be(0);
    }

    [Fact]
    public async Task Swap_with_an_empty_side_moves_the_value_to_the_other_side()
    {
        await NewModule("lhs");
        var rev = await MustAdd("lhs", FieldType.Reference, RefTo("lhs") with { ReadOnly = true }, "沖銷自");
        var debit = await MustAdd("lhs", FieldType.Decimal, label: "借");
        var credit = await MustAdd("lhs", FieldType.Decimal, label: "貸");
        await Send(new ActivateModuleRequest("lhs"));
        (await Send(new SaveLedgerRequest("lhs", new LedgerInput(rev.Id, [], [[debit.Id, credit.Id]], false)))).IsSuccess.Should().BeTrue();
        var title = (await Detail("lhs")).TitleFieldId!.Value;
        var original = await Add("lhs", Vals((title, "\"x\""), (credit.Id, "10")));   // 借方沒有值

        var r = await Send(new ReverseRecordRequest(original.Id, null));

        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var reversal = (DynamicRecordResponse)r.Data!;
        reversal.Values[K(debit.Id)]!.Value.GetDecimal().Should().Be(10);
        (!reversal.Values.TryGetValue(K(credit.Id), out var c) || c is null || c.Value.ValueKind == JsonValueKind.Null).Should().BeTrue();   // 貸方清空
    }

    [Fact]
    public async Task Debit_and_credit_of_a_detail_module_can_be_swapped_and_the_lines_are_mirrored()
    {
        await NewModule("lhv-head");
        var rev = await MustAdd("lhv-head", FieldType.Reference, RefTo("lhv-head") with { ReadOnly = true }, "沖銷自");
        await NewModule("lhv-line");
        await MustAdd("lhv-line", FieldType.Reference, RefTo("lhv-head", ReferenceRelation.Detail), "表頭");
        var debit = await MustAdd("lhv-line", FieldType.Decimal, label: "借");
        var credit = await MustAdd("lhv-line", FieldType.Decimal, label: "貸");
        await Send(new ActivateModuleRequest("lhv-line"));
        await Send(new ActivateModuleRequest("lhv-head"));

        var saved = await Send(new SaveLedgerRequest("lhv-head", new LedgerInput(rev.Id, [], [[debit.Id, credit.Id]], false)));
        saved.IsSuccess.Should().BeTrue(saved.Message + string.Join(";", saved.Errors.Select(e => e.Key + e.Value[0])));

        var title = (await Detail("lhv-head")).TitleFieldId!.Value;
        var lineTitle = (await Detail("lhv-line")).TitleFieldId!.Value;
        var original = await Add("lhv-head", Vals((title, "\"v\"")), new()
        {
            ["lhv-line"] =
            [
                new DetailRowInput(null, Vals((lineTitle, "\"l1\""), (debit.Id, "100")), null),
                new DetailRowInput(null, Vals((lineTitle, "\"l2\""), (credit.Id, "100")), null)
            ]
        });

        var r = await Send(new ReverseRecordRequest(original.Id, null));

        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var lines = ((DynamicRecordResponse)r.Data!).Details!["lhv-line"];
        lines.Should().HaveCount(2);
        lines.Count(l => l.Values.TryGetValue(K(credit.Id), out var v) && v is { ValueKind: JsonValueKind.Number } n && n.GetDecimal() == 100).Should().Be(1);
        lines.Count(l => l.Values.TryGetValue(K(debit.Id), out var v) && v is { ValueKind: JsonValueKind.Number } n && n.GetDecimal() == 100).Should().Be(1);
        // 原本借 100 的那列沖銷後變貸 100，原本貸 100 的變借 100
        var byTitle = lines.ToDictionary(l => l.Values[K(lineTitle)]!.Value.GetString()!);
        byTitle["l1"].Values[K(credit.Id)]!.Value.GetDecimal().Should().Be(100);
        byTitle["l2"].Values[K(debit.Id)]!.Value.GetDecimal().Should().Be(100);
    }
}
