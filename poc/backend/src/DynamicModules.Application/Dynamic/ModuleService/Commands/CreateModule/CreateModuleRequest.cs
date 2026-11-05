using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;

public record CreateModuleRequest(string Key, I18nText Name, string? Icon, string? Section,
    ModuleKind Kind = ModuleKind.Standalone, string? HostEntityKey = null,
    bool CreateTitleField = true) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    // CreateTitleField：false 時不建預設的「名稱」欄位（從範本建立用）；WebAPI 的建立模組端點不開放。
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
