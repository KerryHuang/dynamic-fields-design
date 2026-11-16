using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>流程圖定義的 JSON 形狀（流程圖設計 §7.1）；sequence 的 JSON 與流程圖加入前逐字相同。</summary>
public class ApprovalGraphJsonTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static I18nText T(string s) => new([new("zh-TW", s)]);

    private static ApprovalDefinition SequenceSample()
    {
        var when = JsonSerializer.SerializeToElement(new { all = new[] { new { field = 21, op = "gte", value = 10000 } } });
        ApprovalStageDef Stage(string code, string name, long[] editable) => new(code, T(name), null,
            new ApproverSource(ApprovalSources.Manager, Level: 1), ApprovalModes.Any, [.. editable], 24, ApprovalOnEmpty.Fallback);
        var s2 = new ApprovalStageDef("fin", T("財務"), when, new ApproverSource(ApprovalSources.Group, GroupIds: [6]),
            ApprovalModes.All, [31], null, ApprovalOnEmpty.Skip);
        return new ApprovalDefinition(ApprovalShapes.Sequence, when, 11, new ApproverSource(ApprovalSources.Group, GroupIds: [7]),
            [Stage("mgr", "直屬主管", []), s2]);
    }

    // 流程圖加入前以現行程式序列化得到的字串（Web 預設選項）。
    private const string SequenceBaseline = """
{"shape":"sequence","when":{"all":[{"field":21,"op":"gte","value":10000}]},"rejectTo":11,"fallbackApprovers":{"source":"group","userIds":null,"groupIds":[7],"level":null,"fieldId":null,"candidateGroupIds":null,"min":null,"max":null,"unmatchedGroups":null,"candidatesUnmatched":false},"stages":[{"code":"mgr","name":{"zh-TW":"\u76F4\u5C6C\u4E3B\u7BA1"},"when":null,"approvers":{"source":"manager","userIds":null,"groupIds":null,"level":1,"fieldId":null,"candidateGroupIds":null,"min":null,"max":null,"unmatchedGroups":null,"candidatesUnmatched":false},"mode":"any","editableFields":[],"dueHours":24,"onEmpty":"fallback"},{"code":"fin","name":{"zh-TW":"\u8CA1\u52D9"},"when":{"all":[{"field":21,"op":"gte","value":10000}]},"approvers":{"source":"group","userIds":null,"groupIds":[6],"level":null,"fieldId":null,"candidateGroupIds":null,"min":null,"max":null,"unmatchedGroups":null,"candidatesUnmatched":false},"mode":"all","editableFields":[31],"dueHours":null,"onEmpty":"skip"}]}
""";

    [Fact]
    public void Sequence_SerializesExactlyAsBefore() =>
        JsonSerializer.Serialize(SequenceSample(), WebOptions).Should().Be(SequenceBaseline);

    [Fact]
    public void Sequence_DoesNotWriteNodesOrEdges()
    {
        var json = JsonSerializer.Serialize(SequenceSample());
        json.Should().NotContain("odes").And.NotContain("dges");
    }

    private const string DesignExample = """
    {
      "shape": "graph",
      "when": { "all": [ { "field": 21, "op": "gte", "value": 10000 } ] },
      "rejectTo": 11,
      "fallbackApprovers": { "source": "group", "groupIds": [7] },
      "nodes": [
        { "id": "start", "kind": "start" },
        { "id": "mgr", "kind": "approval", "name": { "zh-TW": "直屬主管" },
          "when": null, "approvers": { "source": "manager", "level": 1 }, "mode": "any",
          "editableFields": [], "dueHours": 24, "onEmpty": "fallback" },
        { "id": "amount", "kind": "condition", "name": { "zh-TW": "金額分級" } },
        { "id": "split", "kind": "fork", "name": { "zh-TW": "法務與財務" } },
        { "id": "legal", "kind": "approval", "name": { "zh-TW": "法務" },
          "when": null, "approvers": { "source": "group", "groupIds": [5] }, "mode": "any",
          "editableFields": [], "dueHours": 48, "onEmpty": "fallback" },
        { "id": "fin", "kind": "approval", "name": { "zh-TW": "財務" },
          "when": null, "approvers": { "source": "group", "groupIds": [6] }, "mode": "any",
          "editableFields": [31], "dueHours": 48, "onEmpty": "fallback" },
        { "id": "merge", "kind": "join", "forkId": "split", "mode": "all" },
        { "id": "tell-hr", "kind": "notify", "name": { "zh-TW": "知會人事" },
          "recipients": [ { "kind": "group", "groupIds": [9] }, { "kind": "initiator" } ],
          "template": { "title": { "zh-TW": "{title} 已過主管核准" },
                        "body": { "zh-TW": "申請金額 {f:21}，請備查。" } } },
        { "id": "end", "kind": "end" }
      ],
      "edges": [
        { "id": "e1", "from": "start", "to": "mgr" },
        { "id": "e2", "from": "mgr", "to": "amount" },
        { "id": "e3", "from": "amount", "to": "split", "order": 1, "label": { "zh-TW": "十萬以上" },
          "when": { "all": [ { "field": 21, "op": "gte", "value": 100000 } ] } },
        { "id": "e4", "from": "amount", "to": "tell-hr", "default": true, "label": { "zh-TW": "其他" } },
        { "id": "e5", "from": "split", "to": "legal" },
        { "id": "e6", "from": "split", "to": "fin" },
        { "id": "e7", "from": "legal", "to": "merge" },
        { "id": "e8", "from": "fin", "to": "merge" },
        { "id": "e9", "from": "merge", "to": "tell-hr" },
        { "id": "e10", "from": "tell-hr", "to": "end" }
      ]
    }
    """;

    [Fact]
    public void Graph_DesignExample_RoundTrips()
    {
        var d = JsonSerializer.Deserialize<ApprovalDefinition>(DesignExample, WebOptions)!;
        d.Shape.Should().Be(ApprovalShapes.Graph);
        d.Nodes.Should().HaveCount(9);
        d.Edges.Should().HaveCount(10);
        d.Stages.Should().BeEmpty();
        var merge = d.Nodes!.Single(n => n.Kind == ApprovalNodeKinds.Join);
        merge.ForkId.Should().Be("split");
        merge.Mode.Should().Be("all");
        d.Nodes!.Single(n => n.Id == "tell-hr").Recipients.Should().HaveCount(2);
        d.Edges!.Single(e => e.Id == "e4").Default.Should().BeTrue();

        var again = JsonSerializer.Deserialize<ApprovalDefinition>(JsonSerializer.Serialize(d), WebOptions)!;
        ApprovalDefinitionJson.SameAs(d, again).Should().BeTrue();
    }

    [Fact]
    public void Graph_MissingStages_EmptyList()
    {
        var d = JsonSerializer.Deserialize<ApprovalDefinition>("""{"shape":"graph","nodes":[],"edges":[]}""", WebOptions)!;
        d.Stages.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Sequence_NullStagesInJson_EmptyList()
    {
        var d = JsonSerializer.Deserialize<ApprovalDefinition>("""{"shape":"sequence","stages":null}""", WebOptions)!;
        d.Stages.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Graph_WritesEmptyStages_AndOmitsNullNodeAttributes()
    {
        var d = new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [],
            [new ApprovalNodeDef("start", ApprovalNodeKinds.Start)], [new ApprovalEdgeDef("e1", "start", "end")]);
        JsonSerializer.Serialize(d, WebOptions).Should().Contain("\"stages\":[]")
            .And.Contain("{\"id\":\"start\",\"kind\":\"start\"}").And.Contain("{\"id\":\"e1\",\"from\":\"start\",\"to\":\"end\"}");
    }

    [Fact]
    public void ApprovalNode_AsStage_CopiesApprovalAttributes()
    {
        var when = JsonSerializer.SerializeToElement(new { all = new[] { new { field = 1, op = "gte", value = 5 } } });
        var approvers = new ApproverSource(ApprovalSources.Group, GroupIds: [5]);
        var n = new ApprovalNodeDef("legal", ApprovalNodeKinds.Approval, T("法務"), when, approvers, ApprovalModes.All, [31], 48, ApprovalOnEmpty.Skip);
        var s = n.AsStage();
        s.Code.Should().Be("legal");
        s.Name.Resolve("zh-TW").Should().Be("法務");
        s.When!.Value.GetRawText().Should().Be(when.GetRawText());
        s.Approvers.Should().BeSameAs(approvers);
        s.Mode.Should().Be(ApprovalModes.All);
        s.EditableFields.Should().Equal(31);
        s.DueHours.Should().Be(48);
        s.OnEmpty.Should().Be(ApprovalOnEmpty.Skip);
    }

    [Fact]
    public void ApprovalNode_AsStage_NullAttributes_FailClosedDefaults()
    {
        var s = new ApprovalNodeDef("a", ApprovalNodeKinds.Approval).AsStage();
        s.EditableFields.Should().BeEmpty();
        s.Name.Should().NotBeNull();
        s.Mode.Should().Be(ApprovalModes.All);
        s.OnEmpty.Should().Be(ApprovalOnEmpty.Fallback);
    }
}
