using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>全部政策：登記資訊、全系統值、出廠值（設計 §7）。</summary>
public sealed record GetSystemPoliciesRequest : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>設全系統值；Value 為 null 表示回到出廠值。</summary>
public sealed record UpdateSystemPolicyRequest(string Code, JsonElement? Value)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;

/// <summary>可模組覆寫的政策：全系統值與覆寫值。</summary>
public sealed record GetModulePoliciesRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

/// <summary>設模組覆寫值；Value 為 null 表示取消覆寫。共用主檔模組另要系統管理員（比照其他設計器寫入）。</summary>
public sealed record UpdateModulePolicyRequest(string ModuleKey, string Code, JsonElement? Value)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>生效值（不含誰設的），任何登入者可讀；ModuleKey 為 null 時只取全系統值。</summary>
public sealed record GetEffectivePoliciesRequest(string? ModuleKey) : IRequest<ServiceResult>;
