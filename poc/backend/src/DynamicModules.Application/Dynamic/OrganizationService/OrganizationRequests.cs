using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.OrganizationService;

/// <summary>登入者都可讀（簽核設計器與使用者選單要用）；增改刪限系統管理員。</summary>
public record ListDepartmentsRequest : IRequest<ServiceResult>;

public record CreateDepartmentRequest(DepartmentInput Input) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;

public record UpdateDepartmentRequest(long Id, DepartmentInput Input) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;

public record DeleteDepartmentRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;

/// <summary>DepartmentId 為 null 代表移除所屬部門。</summary>
public record SetUserDepartmentRequest(string UserId, long? DepartmentId) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
