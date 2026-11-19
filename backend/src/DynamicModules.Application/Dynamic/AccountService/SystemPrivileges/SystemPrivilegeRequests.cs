using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.SystemPrivileges;

/// <summary>
/// 整份取代一個群組的系統權限直接設定（權限軸設計 §8）：只有系統管理員能授予或收回（權限軸-1），
/// 冗餘宣告權限設定讓寫入許可經 gate 開（T7）；系統代碼列要 SystemAdminVerified。
/// </summary>
public record UpdateGroupSystemPrivilegesRequest(long GroupId, SystemPrivilegesDto Body)
    : IRequest<ServiceResult>, ISystemAdminRequest, IPermissionAdminRequest, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("group.systemPrivileges", AuditTargets.Group, GroupId);
}

/// <summary>整份取代一位使用者個人的系統權限直接設定；目標是系統管理員也照存（管理員旗標取消後才生效）。</summary>
public record UpdateUserSystemPrivilegesRequest(string UserId, SystemPrivilegesDto Body)
    : IRequest<ServiceResult>, ISystemAdminRequest, IPermissionAdminRequest, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.systemPrivileges", AuditTargets.User, UserId);
}
