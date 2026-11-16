using DynamicModules.Application.Dynamic.DeleteService;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;

public class DeleteModuleValidator : AbstractValidator<DeleteModuleRequest>
{
    public DeleteModuleValidator() =>
        RuleFor(x => x.Purged).NotNull().GreaterThanOrEqualTo(0)
            .OverridePropertyName("purged").WithMessage(DeleteMessages.PurgedRequired);
}
