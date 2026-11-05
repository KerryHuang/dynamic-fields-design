using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetPreviewSchema;

/// <summary>設計器預覽：以系統管理員權限組出目前草稿的 schema（不看啟用狀態）。</summary>
public record GetPreviewSchemaRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
