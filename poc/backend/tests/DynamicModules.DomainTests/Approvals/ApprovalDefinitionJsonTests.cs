using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>流程定義比對：設計器存檔時用來判斷「定義有沒有改」（只改啟停、名稱、優先序不產生新版本）。</summary>
public class ApprovalDefinitionJsonTests
{
    private static ApprovalDefinition Make(int min = 100, params long[] editable)
    {
        var when = JsonSerializer.SerializeToElement(new { all = new[] { new { field = 1, op = "gte", value = min } } });
        var stage = new ApprovalStageDef("s1", new I18nText([new("zh-TW", "一關")]), null,
            new ApproverSource(ApprovalSources.Manager, Level: 1), ApprovalModes.Any, [.. editable], null, ApprovalOnEmpty.Fallback);
        return new ApprovalDefinition(ApprovalShapes.Sequence, when, null, null, [stage]);
    }

    [Fact]
    public void SameAs_IdenticalAfterRoundTrip_True()
    {
        var a = Make(100, 1, 2);
        var b = JsonSerializer.Deserialize<ApprovalDefinition>(JsonSerializer.Serialize(a))!;
        ApprovalDefinitionJson.SameAs(a, b).Should().BeTrue();
    }

    [Fact]
    public void SameAs_StageConditionDiffers_False() => ApprovalDefinitionJson.SameAs(Make(100), Make(50)).Should().BeFalse();

    [Fact]
    public void SameAs_EditableFieldsOrderDiffers_False() => ApprovalDefinitionJson.SameAs(Make(100, 1, 2), Make(100, 2, 1)).Should().BeFalse();
}
