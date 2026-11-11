using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

public record GetTaskRulesRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

/// <summary>AppId 由套件開啟時帶入（t4b）；設計器端點不帶。</summary>
public record CreateTaskRuleRequest(string ModuleKey, TaskRuleInput Input, long? AppId = null)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>規則代碼不能改；規則必須屬於 ModuleKey 這個模組，否則 404。</summary>
public record UpdateTaskRuleRequest(string ModuleKey, string Key, TaskRuleInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteTaskRuleRequest(string ModuleKey, string Key)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>記錄詳細頁的「套用任務範本」：只收啟用中、應用程式開啟中的 manual 規則。成功時 Data 是 <see cref="TaskService.RecordTasksResponse"/>。</summary>
public sealed record ApplyTaskRuleRequest(long RecordId, string RuleKey) : IRequest<ServiceResult>, ITransactionalRequest;
