using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Queries.ExportTemplate;

/// <summary>把模組（含它的明細模組）匯出成範本檔；成功時 Data 是 ExportFile。</summary>
public record ExportTemplateRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
