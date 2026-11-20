using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;

public class AddOptionValidator : AbstractValidator<AddOptionRequest>
{
    public AddOptionValidator(RequiredTextGuard guard)
    {
        RuleFor(x => x.Label).ValidLabel(guard, isNew: true);
        RuleFor(x => x.Color).MaximumLength(20).WithMessage("顏色最多 20 個字元");
    }
}
