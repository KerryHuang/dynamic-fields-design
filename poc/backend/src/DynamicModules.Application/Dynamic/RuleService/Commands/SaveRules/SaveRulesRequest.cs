using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RuleService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;

public record SaveRulesRequest(string ModuleKey, List<RuleInput> Rules)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
