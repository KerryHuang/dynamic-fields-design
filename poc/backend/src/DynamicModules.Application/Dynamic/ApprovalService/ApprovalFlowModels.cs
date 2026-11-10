using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

public sealed record ApprovalFlowInput(string Key, I18nText Name, long ToOptionId, int Priority, bool Active, ApprovalDefinition Definition);

/// <summary>Version 是目前版本號；Used：任何版本被實例綁定或出現在快照裡；PendingCount：綁定這個流程的進行中實例數。</summary>
public sealed record ApprovalFlowResponse(
    string Key, I18nText Name, string ModuleKey, long ToOptionId, int Priority, bool Active,
    int Version, bool Used, int PendingCount, ApprovalDefinition Definition);
