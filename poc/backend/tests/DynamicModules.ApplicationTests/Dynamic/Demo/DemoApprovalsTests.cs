using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>Demo 在套件格式 8 之前補的設計案簽核流程（見 DemoApprovals）：流程定義、狀態機標記、只有這一個流程、設計群組的權限。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DemoApprovalsTests(DemoGeneratedFixture fx)
{
    private const string Case = "crm-design-case";

    private async Task<long> Option(string label) => long.Parse((await fx.Opt(Case, "狀態", label))[4..]);

    [Fact]
    public async Task DesignCaseFlow_MatchesDefinition()
    {
        fx.Generated();
        var r = await fx.As("admin", new GetApprovalFlowRequest(DemoApprovals.DesignCaseFlowKey));
        r.IsSuccess.Should().BeTrue(r.Message);
        var flow = (ApprovalFlowResponse)r.Data!;

        flow.ModuleKey.Should().Be(Case);
        flow.ToOptionId.Should().Be(await Option("待主管確認"));
        flow.Active.Should().BeTrue();
        flow.Version.Should().Be(1);
        flow.Priority.Should().Be(1);
        flow.Definition.RejectTo.Should().Be(await Option("設計中"));
        flow.Definition.When.Should().BeNull();
        flow.Definition.FallbackApprovers.Should().BeNull("省略時由系統管理員頂替");
        var stage = flow.Definition.Stages.Should().ContainSingle().Subject;
        stage.Code.Should().Be("owner-manager");
        stage.Approvers.Source.Should().Be("manager");
        stage.Approvers.Level.Should().Be(1);
        stage.Mode.Should().Be("any");
        stage.OnEmpty.Should().Be("fallback");
        stage.DueHours.Should().BeNull();
        stage.EditableFields.Should().BeEmpty();
    }

    [Fact]
    public async Task DesignCaseWorkflow_OnlyReviewEntryNeedsApproval()
    {
        fx.Generated();
        var wf = (WorkflowResponse)(await fx.As("admin", new GetWorkflowRequest(Case))).Data!;

        var needs = wf.Transitions.Where(t => t.Approval is { Required: true }).ToList();
        var only = needs.Should().ContainSingle().Subject;
        only.From.Should().Be(await Option("客戶確認中"));
        only.To.Should().Be(await Option("待主管確認"));
        only.Approval!.NoMatch.Should().Be("block");
        wf.LockedStates.Should().BeEquivalentTo([await Option("待主管確認"), await Option("結案")]);
    }

    [Fact]
    public async Task DemoHasNoOtherFlows()
    {
        fx.Generated();
        var keys = await fx.Host.Db.Context.Set<ApprovalFlow>().AsNoTracking().Select(f => f.Key).ToListAsync();
        keys.Should().Equal(DemoApprovals.DesignCaseFlowKey);
    }

    [Fact]
    public async Task CrmDesignerGroupMappedToDemoDesign()
    {
        fx.Generated();
        var mod = await fx.Module(Case);
        var titleKey = $"f_{mod.TitleFieldId}";

        (await fx.Records(Case, "designer01")).Should().NotBeNull();
        using (ActingScope.Begin("designer01"))
        {
            var create = await fx.Host.Send(new CreateRecordRequest(Case, new Dictionary<string, System.Text.Json.JsonElement?>
            {
                [titleKey] = System.Text.Json.JsonSerializer.SerializeToElement("不該建得起來")
            }));
            create.IsForbidden.Should().BeTrue("設計群組對設計案沒有新增權限");
        }
    }
}
