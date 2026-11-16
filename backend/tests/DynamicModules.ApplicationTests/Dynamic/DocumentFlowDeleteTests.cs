using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>安全刪除要認得三項單據能力的引用：轉換定義、帶值對應、狀態機的狀態欄位。</summary>
public class DocumentFlowDeleteTests : ConversionTestBase
{
    private async Task<DeleteCheckResponse> Check(DeleteTarget target, string? key, long id) =>
        (DeleteCheckResponse)(await Send(new CheckDeleteRequest(target, key, id))).Data!;

    [Fact]
    public async Task DeletingTheTargetModuleOfAConversion_IsBlocked()
    {
        var c = await BuildChain("d");
        await DefineOk(c, "d-to");
        var check = await Check(DeleteTarget.Module, c.Dst, 0);
        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
    }

    [Fact]
    public async Task DeletingAFieldUsedByAConversionMapping_IsBlocked()
    {
        var c = await BuildChain("d");
        await DefineOk(c, "d-to");
        // 狀態欄位沒出現在任何對應裡；備註（表頭對應）、品項（明細對應）、追溯欄位都有
        (await Check(DeleteTarget.Field, null, c.SrcStatus)).Blockers.Should().NotContain(b => b.Kind == DeleteBlockerKind.Conversion);
        (await Check(DeleteTarget.Field, null, c.SrcMemo)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
        (await Check(DeleteTarget.Field, null, c.LineItem)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
        (await Check(DeleteTarget.Field, null, c.DstLineFrom)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
    }

    [Fact]
    public async Task DeletingTheStatusFieldOfAWorkflow_IsBlocked()
    {
        var c = await BuildChain("d");
        var check = await Check(DeleteTarget.Field, null, c.SrcStatus);
        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Workflow);
    }

    [Fact]
    public async Task DeletingAFieldUsedByAFillMapping_IsBlocked()
    {
        var c = await BuildChain("d");
        var partyRef = (await Detail(c.Dst)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == c.DstParty);
        var fill = new FillConfig(FillConfig.Default, [new FillMapping(c.PartyName, c.DstMemo)]);
        (await Update(partyRef, partyRef.Config with { Fill = fill })).IsSuccess.Should().BeTrue();
        (await Check(DeleteTarget.Field, null, c.PartyName)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Fill);
        (await Check(DeleteTarget.Field, null, c.DstMemo)).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Fill);
    }
}
