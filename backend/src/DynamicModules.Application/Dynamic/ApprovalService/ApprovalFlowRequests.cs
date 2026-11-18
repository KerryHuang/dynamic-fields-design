using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
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
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.create", AuditTargets.ApprovalFlow, Input?.Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>流程代碼與模組不能改；目標狀態只有沒被用過的流程能改。</summary>
public record UpdateApprovalFlowRequest(string Key, ApprovalFlowInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.update", AuditTargets.ApprovalFlow, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteApprovalFlowRequest(string Key)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.delete", AuditTargets.ApprovalFlow, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>把目標狀態上的流程優先序改成 Keys 的順序（1..n）；Keys 必須剛好是這個目標狀態上的全部流程。</summary>
public record ReorderApprovalFlowsRequest(string ModuleKey, long ToOptionId, List<string> Keys)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.reorder", AuditTargets.Module, ModuleKey);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>乾跑驗證（J64）：跑與發布相同的檢查、不寫入。Key 有值＝既有流程（必須屬於 ModuleKey）；沒值＝新流程。</summary>
public record ValidateApprovalFlowRequest(string ModuleKey, string? Key, ApprovalFlowDraftInput Input)
    : IRequest<ServiceResult>, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets =>
        Key is null ? [new(ModuleKey: ModuleKey)] : [new(ModuleKey: ModuleKey), new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>建立只有草稿的新流程（沒有版本、強制停用）。</summary>
public record CreateApprovalFlowDraftRequest(string ModuleKey, ApprovalFlowDraftInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.createDraft", AuditTargets.ApprovalFlow, Input?.Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>儲存草稿，同時套用名稱與目標狀態（U53）。</summary>
public record SaveApprovalFlowDraftRequest(string Key, ApprovalFlowDraftInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.saveDraft", AuditTargets.ApprovalFlow, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>捨棄草稿，回到已發布的版本；BaseRev 不符且不是 Force 回 409。</summary>
public record DiscardApprovalFlowDraftRequest(string Key, string? BaseRev, bool Force)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.discardDraft", AuditTargets.ApprovalFlow, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>以請求內容發布（§9 全部規則），清掉草稿。</summary>
public record PublishApprovalFlowRequest(string Key, ApprovalFlowDraftInput Input)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("approvalFlow.publish", AuditTargets.ApprovalFlow, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ApprovalFlowKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
