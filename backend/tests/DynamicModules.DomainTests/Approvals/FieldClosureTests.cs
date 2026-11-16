using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>欄位的上游相依閉包：公式、帶值、彙總（簽核設計 §9.2、§9.3）。</summary>
public class FieldClosureTests
{
    private const long Total = 1, Subtotal = 2, Tax = 3, Rate = 4, Status = 5, Supplier = 6, SupplierName = 7, Memo = 8;

    private static Field F(long id, FieldType type, FieldConfig? config = null) =>
        new() { Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active, Config = config ?? new FieldConfig() };

    /// <summary>總額 = 小計 + 稅；稅 = 小計 × 稅率；小計 = 明細彙總；供應商參照帶入供應商名稱。</summary>
    private static Module Doc() => new()
    {
        Id = 1, Key = "doc",
        Blocks =
        [
            new Block
            {
                Fields =
                [
                    F(Total, FieldType.Formula, new FieldConfig { Formula = $"{{{Subtotal}}} + {{{Tax}}}" }),
                    F(Subtotal, FieldType.Rollup, new FieldConfig { Rollup = new RollupConfig(900, RollupFunction.Sum, 901, null) }),
                    F(Tax, FieldType.Formula, new FieldConfig { Formula = $"{{{Subtotal}}} * {{{Rate}}}" }),
                    F(Rate, FieldType.Decimal),
                    F(Status, FieldType.Enum),
                    F(Supplier, FieldType.Reference, new FieldConfig
                    {
                        RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "supplier"),
                        Fill = new FillConfig(FillConfig.Lock, [new FillMapping(50, SupplierName)])
                    }),
                    F(SupplierName, FieldType.Text),
                    F(Memo, FieldType.Text),
                ]
            }
        ]
    };

    [Fact]
    public void Closure_FormulaChain()
    {
        var r = FieldClosure.Upstream(Doc(), [Total], Status);

        r.FieldIds.Should().BeEquivalentTo([Total, Subtotal, Tax, Rate]);
        r.IncludesOwnDetailRollup.Should().BeTrue();
        r.Paths[Rate].Should().Equal(Total, Tax, Rate);
        r.Paths[Total].Should().Equal(Total);
    }

    [Fact]
    public void Closure_RollupFromOtherModule_NotOwnDetail()
    {
        var r = FieldClosure.Upstream(Doc(), [Total], Status, isOwnDetailRollup: _ => false);

        r.FieldIds.Should().Contain(Subtotal);
        r.IncludesOwnDetailRollup.Should().BeFalse();
    }

    [Fact]
    public void Closure_FillTarget_IncludesReference()
    {
        var r = FieldClosure.Upstream(Doc(), [SupplierName], Status);

        r.FieldIds.Should().BeEquivalentTo([SupplierName, Supplier]);
        r.Paths[Supplier].Should().Equal(SupplierName, Supplier);
    }

    [Fact]
    public void Closure_ExcludesStatusField()
    {
        var r = FieldClosure.Upstream(Doc(), [Status, Memo], Status);

        r.FieldIds.Should().BeEquivalentTo([Memo]);
        r.IncludesOwnDetailRollup.Should().BeFalse();
    }

    [Fact]
    public void DirectFields_WhenStagesAndUserField()
    {
        var cond = (long f) => JsonDocument.Parse($$"""{"all":[{"field":{{f}},"op":"gte","value":1}]}""").RootElement.Clone();
        var d = new ApprovalDefinition(ApprovalShapes.Sequence, cond(Total), null, null,
        [
            new ApprovalStageDef("a", new I18nText(), cond(Rate), new ApproverSource(ApprovalSources.UserField, FieldId: 77, CandidateGroupIds: [1]),
                ApprovalModes.Any, [Memo], null, ApprovalOnEmpty.Fallback),
            new ApprovalStageDef("b", new I18nText(), null, new ApproverSource(ApprovalSources.Group, GroupIds: [1]),
                ApprovalModes.Any, [], null, ApprovalOnEmpty.Fallback),
        ]);

        FieldClosure.DirectFields(d).Should().BeEquivalentTo([Total, Rate, 77L]);
    }
}
