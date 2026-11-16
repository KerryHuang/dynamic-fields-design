using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

public record GetApprovalFlowsRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public record GetApprovalFlowRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public record CreateApprovalFlowRequest(string ModuleKey, ApprovalFlowInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>流程代碼與模組不能改；目標狀態只有沒被用過的流程能改。</summary>
public record UpdateApprovalFlowRequest(string Key, ApprovalFlowInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteApprovalFlowRequest(string Key)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>把目標狀態上的流程優先序改成 Keys 的順序（1..n）；Keys 必須剛好是這個目標狀態上的全部流程。</summary>
public record ReorderApprovalFlowsRequest(string ModuleKey, long ToOptionId, List<string> Keys)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
