using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;

public class DeactivateModuleValidator : AbstractValidator<DeactivateModuleRequest>
{
    public DeactivateModuleValidator() => RuleFor(x => x.Key).NotEmpty().WithMessage("模組代碼為必填");
}
