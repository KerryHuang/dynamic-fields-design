using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// 套件格式 8（簽核 a5）之前，Demo 用既有 API 補設計案的簽核流程。a5 讓 crm-sales 套件內建這個流程後刪除這個類別
/// （見 a6 計畫「交給 a5」）。呼叫端在 admin 範圍與交易內；失敗回 ServiceResult，由 DemoStructure.Fail 加步驟名。
/// 順序：先建流程（目標狀態要有流程才能標需簽核），再把「客戶確認中 → 待主管確認」標成需簽核。
/// 這條路徑直接呼叫建流程與存狀態機的指令，不經套件安裝器，所以不受安裝器清除轉移簽核設定的影響。
/// </summary>
public sealed class DemoApprovals(ISender sender, IModuleRepository modules)
{
    public const string DesignCaseFlowKey = "crm-design-case-review";

    private const string Module = "design-case";

    public async Task<ServiceResult?> CreateAsync(TemplateIdMap crm, CancellationToken ct)
    {
        long Option(string code) => crm.OptionIds[$"{Module}.{code}"];
        var key = crm.ModuleKeys[Module];

        var definition = new ApprovalDefinition(
            "sequence", null, Option("designing"), null,
            [new ApprovalStageDef("owner-manager", Name("負責業務主管"), null, new ApproverSource(ApprovalSources.Manager, Level: 1), "any", [], null, "fallback")]);
        var created = await sender.Send(new CreateApprovalFlowRequest(key,
            new ApprovalFlowInput(DesignCaseFlowKey, Name("設計案主管確認"), Option("manager-review"), 1, true, definition)), ct);
        if (!created.IsSuccess) return created;

        var workflow = (await modules.GetByKeyAsync(key, true, ct))?.Workflow;
        if (workflow is null) return ServiceResult.NotFound("設計案沒有狀態機");
        var from = Option("customer-review");
        var to = Option("manager-review");
        if (workflow.Find(from, to) is null) return ServiceResult.NotFound("設計案沒有「客戶確認中 → 待主管確認」的轉移");
        var transitions = workflow.Transitions
            .Select(t => t.From == from && t.To == to ? t with { Approval = new TransitionApproval(true, "block") } : t).ToList();
        var saved = await sender.Send(new SaveWorkflowRequest(key,
            new WorkflowInput(workflow.StatusFieldId, workflow.InitialOptionId, [.. workflow.States], transitions)), ct);
        return saved.IsSuccess ? null : saved;
    }

    private static I18nText Name(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);
}
