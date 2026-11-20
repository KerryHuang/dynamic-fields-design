using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

public class UpdateFieldValidator : AbstractValidator<UpdateFieldRequest>
{
    public UpdateFieldValidator(RequiredTextGuard guard)
    {
        RuleFor(x => x.Label).ValidLabel(guard, isNew: false);
        RuleFor(x => x.Config).Custom((config, ctx) => FieldRules.ValidateConfig(ctx.InstanceToValidate.Type, config, ctx));
    }
}
