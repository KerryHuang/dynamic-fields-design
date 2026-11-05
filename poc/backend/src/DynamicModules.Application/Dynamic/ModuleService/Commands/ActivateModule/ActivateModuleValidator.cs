using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;

public class ActivateModuleValidator : AbstractValidator<ActivateModuleRequest>
{
    public ActivateModuleValidator() => RuleFor(x => x.Key).NotEmpty().WithMessage("模組代碼為必填");
}
