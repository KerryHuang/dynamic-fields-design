using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleValidator : AbstractValidator<UpdateModuleRequest>
{
    public UpdateModuleValidator(RequiredTextGuard guard)
    {
        RuleFor(x => x.Name).ValidModuleName(guard, isNew: false);
        RuleFor(x => x.Section).ValidModuleSection();
    }
}
