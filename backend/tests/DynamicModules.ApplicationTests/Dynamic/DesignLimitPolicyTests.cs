using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Rules;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>設計上限（政策 A16）在設計器存檔的生效：規則數、流程狀態數；調低後編輯既有定義只在數量增加時檢查（§3.5）。</summary>
public class DesignLimitPolicyTests : DesignerTestBase
{
    private async Task SetSystem(PolicyDefinition def, int? value) =>
        (await Send(new UpdateSystemPolicyRequest(def.Code, value is { } v ? JsonSerializer.SerializeToElement(v) : null)))
            .IsSuccess.Should().BeTrue();

    private static List<RuleInput> Rules(long target, int count, string condition = "{\"all\":[]}") =>
        Enumerable.Range(0, count)
            .Select(_ => new RuleInput(RuleKind.Required, J(condition), [$"field:{target}"], null, null, true)).ToList();

    [Fact]
    public async Task SaveRules_UsesPolicy()
    {
        await NewModule("memo");
        var note = await MustAdd("memo", FieldType.Text, label: "備註");
        await SetSystem(PolicyCatalog.DesignMaxRulesPerModule, 2);

        var r = await Send(new SaveRulesRequest("memo", Rules(note.Id, 3)));
        r.IsInvalid.Should().BeTrue();
        r.Errors["rules"][0].Should().Be("規則最多 2 條");
        (await Send(new SaveRulesRequest("memo", Rules(note.Id, 2)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SaveRules_LoweredLimit_EditWithoutIncreaseAllowed()
    {
        await NewModule("memo");
        var note = await MustAdd("memo", FieldType.Text, label: "備註");
        var qty = await MustAdd("memo", FieldType.Integer, label: "數量");
        (await Send(new SaveRulesRequest("memo", Rules(note.Id, 3)))).IsSuccess.Should().BeTrue();
        await SetSystem(PolicyCatalog.DesignMaxRulesPerModule, 2);

        var edited = Rules(note.Id, 3);
        edited[1] = edited[1] with { Condition = J($"{{\"all\":[{{\"field\":{qty.Id},\"op\":\"gt\",\"value\":5}}]}}") };
        var ok = await Send(new SaveRulesRequest("memo", edited));
        ok.IsSuccess.Should().BeTrue(string.Join(";", ok.Errors.Select(e => e.Key + e.Value[0])));

        var more = await Send(new SaveRulesRequest("memo", Rules(note.Id, 4)));
        more.IsInvalid.Should().BeTrue();
        more.Errors["rules"][0].Should().Be("規則最多 2 條");
    }

    [Fact]
    public async Task SaveWorkflow_LoweredStateLimit_EditAllowed()
    {
        await NewModule("doc");
        var status = await MustAdd("doc", FieldType.Enum, label: "狀態");
        var ids = new List<long>();
        foreach (var label in new[] { "草稿", "送審", "核准", "結案" })
            ids.Add(((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text(label), null))).Data!).Id);
        WorkflowInput Input(int states, params (int From, int To)[] transitions) => new(status.Id, ids[0],
            ids.Take(states).Select(id => new WorkflowState(id)).ToList(),
            transitions.Select(t => new WorkflowTransition(ids[t.From], ids[t.To], [])).ToList());

        (await Send(new SaveWorkflowRequest("doc", Input(3, (0, 1))))).IsSuccess.Should().BeTrue();
        await SetSystem(PolicyCatalog.DesignMaxWorkflowStates, 2);

        var edit = await Send(new SaveWorkflowRequest("doc", Input(3, (0, 1), (1, 2))));
        edit.IsSuccess.Should().BeTrue(string.Join(";", edit.Errors.Select(e => e.Key + e.Value[0])));

        var more = await Send(new SaveWorkflowRequest("doc", Input(4, (0, 1), (1, 2))));
        more.IsInvalid.Should().BeTrue();
        more.Errors["states"][0].Should().Contain("1 到 2 個");
    }

    [Fact]
    public async Task SaveWorkflow_LoweredTransitionLimit_EditAllowed_IncreaseRejected()
    {
        await NewModule("doc");
        var status = await MustAdd("doc", FieldType.Enum, label: "狀態");
        var ids = new List<long>();
        foreach (var label in new[] { "草稿", "送審", "核准" })
            ids.Add(((OptionResponse)(await Send(new AddOptionRequest(status.Id, Text(label), null))).Data!).Id);
        WorkflowInput Input(params (int From, int To)[] transitions) => new(status.Id, ids[0],
            ids.Select(id => new WorkflowState(id)).ToList(),
            transitions.Select(t => new WorkflowTransition(ids[t.From], ids[t.To], [])).ToList());

        (await Send(new SaveWorkflowRequest("doc", Input((0, 1), (1, 2))))).IsSuccess.Should().BeTrue();
        await SetSystem(PolicyCatalog.DesignMaxWorkflowTransitions, 1);

        (await Send(new SaveWorkflowRequest("doc", Input((0, 1), (1, 0))))).IsSuccess.Should().BeTrue();
        var more = await Send(new SaveWorkflowRequest("doc", Input((0, 1), (1, 2), (2, 0))));
        more.IsInvalid.Should().BeTrue();
        more.Errors["transitions"].Should().Equal("每個流程最多 1 條轉換");

        // 條數沒超過時，其他轉換錯誤仍是原本的共用句（使用者 2026-11-20 裁決只有條數另給帶上限的訊息）
        var self = await Send(new SaveWorkflowRequest("doc", Input((0, 0))));
        self.IsInvalid.Should().BeTrue();
        self.Errors["transitions"].Should().Equal("轉移的兩端必須是不同的狀態、不重複，群組必須存在");
    }
}
