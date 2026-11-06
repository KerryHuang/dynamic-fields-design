using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;

/// <summary>刪除預檢。Module 目標用 Key，Field 與 Block 目標用 Id。需要設計器的 Delete 權限（預檢會洩漏定義結構）。</summary>
public record CheckDeleteRequest(DeleteTarget Target, string? Key, long Id) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}
