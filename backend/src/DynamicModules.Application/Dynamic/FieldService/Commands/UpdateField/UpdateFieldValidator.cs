using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

public class UpdateFieldValidator : AbstractValidator<UpdateFieldRequest>
{
    public UpdateFieldValidator()
    {
        RuleFor(x => x.Label).ValidLabel();
        RuleFor(x => x.Config).Custom((config, ctx) => FieldRules.ValidateConfig(ctx.InstanceToValidate.Type, config, ctx));
    }
}
