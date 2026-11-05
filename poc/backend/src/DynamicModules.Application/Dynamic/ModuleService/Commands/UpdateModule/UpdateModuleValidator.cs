using DynamicModules.Application.Dynamic.ModuleService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;

public class UpdateModuleValidator : AbstractValidator<UpdateModuleRequest>
{
    public UpdateModuleValidator()
    {
        RuleFor(x => x.Name).ValidModuleName();
        RuleFor(x => x.Section).MaximumLength(100).WithMessage("選單分區不能超過 100 字");
    }
}
