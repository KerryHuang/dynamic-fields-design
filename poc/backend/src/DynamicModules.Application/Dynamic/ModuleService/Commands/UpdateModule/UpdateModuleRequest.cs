using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

/// <param name="TitleTemplate">標題樣板，{} 內是欄位 Id（例如 {12} {13}）；null 表示不用樣板。</param>
public record UpdateModuleRequest(string Key, I18nText Name, string? Icon, string? Section, long? TitleFieldId, string? TitleTemplate = null) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
