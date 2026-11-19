using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;

public record CreateModuleRequest(string Key, I18nText Name, string? Icon, I18nText? Section,
    ModuleKind Kind = ModuleKind.Standalone, string? HostEntityKey = null,
    bool? CreateTitleField = null) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("module.create", AuditTargets.Module, Key);
    // CreateTitleField：null＝依政策 module.createTitleField；範本建立固定傳 false（不建預設的「名稱」欄位）。WebAPI 的建立模組端點不開放。
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
