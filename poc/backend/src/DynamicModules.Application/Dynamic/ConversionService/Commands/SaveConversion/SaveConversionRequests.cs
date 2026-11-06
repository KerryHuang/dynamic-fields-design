using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;

public record CreateConversionRequest(ConversionInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>更新時代碼與來源、目標模組不能改（已轉出的單據靠它們追溯）。</summary>
public record UpdateConversionRequest(string Key, ConversionInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
