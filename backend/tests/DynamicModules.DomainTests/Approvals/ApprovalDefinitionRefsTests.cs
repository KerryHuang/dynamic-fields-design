using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>測試用的流程圖定義；各欄位 Id 在常數裡一目了然。</summary>
internal static class GraphSamples
{
    public const long FlowWhenField = 20, CondEdgeField = 21, SkipField = 22, UserFieldId = 23, EditableField = 31, TemplateField = 41, TitleField = 42;
    public const long ApprovalGroup = 5, CandidateGroup = 6, FallbackGroup = 7, NotifyGroup = 9;

    public static I18nText T(string s) => new([new("zh-TW", s)]);

    public static JsonElement Cond(long field) =>
        JsonDocument.Parse($$"""{"all":[{"field":{{field}},"op":"gte","value":1}]}""").RootElement.Clone();

    /// <summary>start → 條件（依 CondEdgeField 分路）→ 簽核「mgr」（略過條件 SkipField、userField、可編輯 EditableField）→ 通知 → end；預設線直接到通知。</summary>
    public static ApprovalDefinition Graph() => new(ApprovalShapes.Graph, Cond(FlowWhenField), 11,
        new ApproverSource(ApprovalSources.Group, GroupIds: [FallbackGroup]), [],
        [
            new ApprovalNodeDef("start", ApprovalNodeKinds.Start),
            new ApprovalNodeDef("amount", ApprovalNodeKinds.Condition, T("金額")),
            new ApprovalNodeDef("mgr", ApprovalNodeKinds.Approval, T("主管"), Cond(SkipField),
                new ApproverSource(ApprovalSources.UserField, FieldId: UserFieldId, CandidateGroupIds: [CandidateGroup], GroupIds: [ApprovalGroup]),
                ApprovalModes.Any, [EditableField], 24, ApprovalOnEmpty.Fallback),
            new ApprovalNodeDef("tell", ApprovalNodeKinds.Notify, T("知會"),
                Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Group, GroupIds: [NotifyGroup]), new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator)],
                Template: new NotifyTemplateDef(
                    new I18nText([new("zh-TW", $"{{title}} {{f:{TitleField}}}"), new("en-US", "{title}")]),
                    new I18nText([new("zh-TW", $"金額 {{f:{TemplateField}}}"), new("en-US", $"{{f:{TemplateField}}} and {{f:{TitleField}}}")]))),
            new ApprovalNodeDef("end", ApprovalNodeKinds.End),
        ],
        [
            new ApprovalEdgeDef("e1", "start", "amount"),
            new ApprovalEdgeDef("e2", "amount", "mgr", T("大額"), Cond(CondEdgeField), 1),
            new ApprovalEdgeDef("e3", "amount", "tell", T("其他"), Default: true),
            new ApprovalEdgeDef("e4", "mgr", "tell"),
            new ApprovalEdgeDef("e5", "tell", "end"),
        ]);
}

/// <summary>流程定義引用的欄位與群組：sequence 與 graph 兩種形狀共用同一組讀取函式（流程圖設計 §16）。</summary>
public class ApprovalDefinitionRefsTests
{
    private static ApprovalDefinition Sequence() => new(ApprovalShapes.Sequence, GraphSamples.Cond(1), null,
        new ApproverSource(ApprovalSources.Group, GroupIds: [7]),
    [
        new ApprovalStageDef("a", GraphSamples.T("a"), GraphSamples.Cond(2), new ApproverSource(ApprovalSources.UserField, FieldId: 77, CandidateGroupIds: [3]),
            ApprovalModes.Any, [8, 9], null, ApprovalOnEmpty.Fallback),
        new ApprovalStageDef("b", GraphSamples.T("b"), null, new ApproverSource(ApprovalSources.Group, GroupIds: [4]),
            ApprovalModes.Any, [], null, ApprovalOnEmpty.Fallback),
    ]);

    [Fact]
    public void Refs_Sequence_SameAsBefore()
    {
        var d = Sequence();
        ApprovalDefinitionRefs.Steps(d).Select(s => s.Code).Should().Equal("a", "b");
        ApprovalDefinitionRefs.Conditions(d).Should().HaveCount(2);
        ApprovalDefinitionRefs.NotifyFieldRefs(d).Should().BeEmpty();
        ApprovalDefinitionRefs.GroupRefs(d).Should().BeEquivalentTo([7L, 3L, 4L]);
        FieldClosure.DirectFields(d).Should().BeEquivalentTo([1L, 2L, 77L]);
        FieldClosure.ReferencedFields(d).Should().BeEquivalentTo([1L, 2L, 77L, 8L, 9L]);
    }

    [Fact]
    public void Steps_Graph_OnlyApprovalNodes_AsStages()
    {
        var steps = ApprovalDefinitionRefs.Steps(GraphSamples.Graph()).ToList();
        steps.Should().ContainSingle().Which.Code.Should().Be("mgr");
    }

    [Fact]
    public void Conditions_Graph_FlowStepAndEdge() =>
        ApprovalDefinitionRefs.Conditions(GraphSamples.Graph()).Should().HaveCount(3);

    [Fact]
    public void DirectFields_Graph_IncludesConditionEdgesSkipConditionsAndUserField_NotTemplate()
    {
        FieldClosure.DirectFields(GraphSamples.Graph()).Should().BeEquivalentTo(
            [GraphSamples.FlowWhenField, GraphSamples.CondEdgeField, GraphSamples.SkipField, GraphSamples.UserFieldId]);
    }

    [Fact]
    public void ReferencedFields_Graph_IncludesEditableAndTemplateFields()
    {
        FieldClosure.ReferencedFields(GraphSamples.Graph()).Should().BeEquivalentTo(
        [
            GraphSamples.FlowWhenField, GraphSamples.CondEdgeField, GraphSamples.SkipField, GraphSamples.UserFieldId,
            GraphSamples.EditableField, GraphSamples.TemplateField, GraphSamples.TitleField
        ]);
    }

    [Fact]
    public void NotifyFieldRefs_Graph_AllLocalesTitleAndBody() =>
        ApprovalDefinitionRefs.NotifyFieldRefs(GraphSamples.Graph()).Distinct().Should().BeEquivalentTo([GraphSamples.TemplateField, GraphSamples.TitleField]);

    [Fact]
    public void GroupRefs_Graph_IncludesNotifyRecipientGroups() =>
        ApprovalDefinitionRefs.GroupRefs(GraphSamples.Graph()).Should().BeEquivalentTo(
            [GraphSamples.FallbackGroup, GraphSamples.CandidateGroup, GraphSamples.ApprovalGroup, GraphSamples.NotifyGroup]);

    [Fact]
    public void Graph_NullCollections_FailClosedNoThrow()
    {
        var d = new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [],
            [new ApprovalNodeDef("a", ApprovalNodeKinds.Approval), new ApprovalNodeDef("n", ApprovalNodeKinds.Notify)], null);
        FieldClosure.ReferencedFields(d).Should().BeEmpty();
        ApprovalDefinitionRefs.GroupRefs(d).Should().BeEmpty();
    }
}
