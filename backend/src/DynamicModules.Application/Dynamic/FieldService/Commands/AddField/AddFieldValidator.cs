using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddField;

public class AddFieldValidator : AbstractValidator<AddFieldRequest>
{
    public AddFieldValidator()
    {
        RuleFor(x => x.Label).ValidLabel();
        RuleFor(x => x.Type).IsInEnum().WithMessage("欄位型別不正確");
        RuleFor(x => x.Config).Custom((config, ctx) => FieldRules.ValidateConfig(ctx.InstanceToValidate.Type, config, ctx));
    }
}
