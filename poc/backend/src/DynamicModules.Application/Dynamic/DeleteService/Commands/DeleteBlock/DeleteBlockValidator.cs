using DynamicModules.Application.Dynamic.DeleteService;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;

public class DeleteBlockValidator : AbstractValidator<DeleteBlockRequest>
{
    public DeleteBlockValidator() =>
        RuleFor(x => x.Purged).NotNull().GreaterThanOrEqualTo(0)
            .OverridePropertyName("purged").WithMessage(DeleteMessages.PurgedRequired);
}
