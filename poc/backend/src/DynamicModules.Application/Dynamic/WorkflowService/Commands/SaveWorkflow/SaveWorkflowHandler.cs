using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;

public class SaveWorkflowHandler(
    IModuleRepository modules, IPermissionRepository permissions, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    IApprovalFlowRepository flows, IEventRepository events, WorkflowApprovals approvals, PendingApprovalGuard pending,
    LockedDataGuard lockedData)
    : IRequestHandler<SaveWorkflowRequest, ServiceResult>
{
    public const int MaxStates = 30;
    public const int MaxTransitions = 200;

    public async Task<ServiceResult> Handle(SaveWorkflowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));
        var input = request.Workflow;

        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (module.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(module))
            Err("moduleKey", "明細模組與擴充模組不能設定狀態機");

        var status = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == input.StatusFieldId);
        if (status is not { Status: FieldStatus.Active, Type: FieldType.Enum })
            Err("statusFieldId", "狀態欄位必須是本模組啟用中的單選下拉欄位");
        else if (status.Config.ReadOnly)
            Err("statusFieldId", "狀態欄位不能設為唯讀，否則沒有人能轉移狀態");

        var optionIds = status?.Options.Where(o => o.Active).Select(o => o.Id).ToHashSet() ?? [];
        var states = input.States ?? [];
        var transitions = input.Transitions ?? [];
        if (states.Count is 0 or > MaxStates || states.Select(s => s.OptionId).Distinct().Count() != states.Count
            || states.Any(s => !optionIds.Contains(s.OptionId)))
            Err("states", $"狀態必須是狀態欄位啟用中的選項，不重複，1 到 {MaxStates} 個");
        var stateIds = states.Select(s => s.OptionId).ToHashSet();
        if (!stateIds.Contains(input.InitialOptionId)) Err("initialOptionId", "起始狀態必須是狀態清單內的狀態");

        var groupIds = (await permissions.ListGroupsAsync(ct)).Select(g => g.Id).ToHashSet();
        if (transitions.Count > MaxTransitions
            || transitions.Any(t => t.From == t.To || !stateIds.Contains(t.From) || !stateIds.Contains(t.To))
            || transitions.Select(t => (t.From, t.To)).Distinct().Count() != transitions.Count
            || transitions.Any(t => t.GroupIds is null || t.GroupIds.Any(g => !groupIds.Contains(g))))
            Err("transitions", "轉移的兩端必須是不同的狀態、不重複，群組必須存在");
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var candidate = new Workflow
        {
            ModuleId = module.Id, StatusFieldId = input.StatusFieldId, InitialOptionId = input.InitialOptionId,
            States = [.. states], Transitions = [.. transitions]
        };
        if (await pending.RejectWorkflowChangeAsync(module, candidate, ct) is { } pendingApproval) return pendingApproval;
        if (await lockedData.RejectUnlockAsync(module, candidate, ct) is { } lockedRecords) return lockedRecords;

        if (await ApprovalErrorsAsync(module, input, candidate, ct) is { Count: > 0 } approvalErrors) return ServiceResult.Invalid(approvalErrors);

        var now = time.Now;
        var workflow = module.Workflow;
        if (workflow is null)
        {
            workflow = new Workflow { ModuleId = module.Id };
            workflow.StampCreated(user.UserId, now);
            module.Workflow = workflow;
        }
        else workflow.StampModified(user.UserId, now);
        workflow.StatusFieldId = input.StatusFieldId;
        workflow.InitialOptionId = input.InitialOptionId;
        workflow.States = [.. states];
        workflow.Transitions = transitions.Select(t => t with { GroupIds = [.. t.GroupIds.Distinct()] }).ToList();

        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(workflow.ToResponse(await approvals.LockedFieldIdsAsync(module, workflow, ct),
            await approvals.EditableFieldIdsAsync(module, workflow, ct)));
    }

    /// <summary>簽核的入口約束與事件交叉檢查（簽核設計 §5.2、§5.3）；沒有需簽核轉移時不查資料庫、一律通過。</summary>
    private async Task<Dictionary<string, string[]>?> ApprovalErrorsAsync(
        Module module, WorkflowInput input, Workflow candidate, CancellationToken ct)
    {
        if (!candidate.Transitions.Any(t => t.Approval is { Required: true })) return null;
        if (candidate.Transitions.Any(t => t.Approval is { NoMatch: not (ApprovalNoMatch.Block or ApprovalNoMatch.Direct) }))
            return new() { ["transitions"] = ["沒有流程符合時的處理只能是 block 或 direct"] };

        var errors = ApprovalConstraints.Validate(module, candidate, await flows.ListCurrentByModuleAsync(module.Id, ct));
        var labels = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == input.StatusFieldId).Options
            .ToDictionary(o => o.Id, o => o.LabelI18n.Resolve("zh-TW"));
        var active = (await events.ListAsync(ct)).Where(e => e.Active);
        foreach (var (key, to) in ApprovalEventCrossCheck.Find(module, candidate, active))
            errors.TryAdd($"events[{key}]", [ApprovalMessages.EventPushes(key, labels.GetValueOrDefault(to) ?? to.ToString())]);
        return errors;
    }
}
