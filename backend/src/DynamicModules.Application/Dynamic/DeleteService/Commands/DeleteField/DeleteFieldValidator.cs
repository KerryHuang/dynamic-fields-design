using DynamicModules.Application.Dynamic.DeleteService;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;

public class DeleteFieldValidator : AbstractValidator<DeleteFieldRequest>
{
    public DeleteFieldValidator() =>
        RuleFor(x => x.Purged).NotNull().GreaterThanOrEqualTo(0)
            .OverridePropertyName("purged").WithMessage(DeleteMessages.PurgedRequired);
}
