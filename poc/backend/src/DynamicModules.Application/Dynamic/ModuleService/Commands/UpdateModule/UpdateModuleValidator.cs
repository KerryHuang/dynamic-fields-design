using DynamicModules.Application.Dynamic.ModuleService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleValidator : AbstractValidator<UpdateModuleRequest>
{
    public UpdateModuleValidator()
    {
        RuleFor(x => x.Name).ValidModuleName();
        RuleFor(x => x.Section).ValidModuleSection();
    }
}
