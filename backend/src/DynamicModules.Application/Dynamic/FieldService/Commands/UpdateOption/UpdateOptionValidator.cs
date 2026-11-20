using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;

public class UpdateOptionValidator : AbstractValidator<UpdateOptionRequest>
{
    public UpdateOptionValidator(RequiredTextGuard guard)
    {
        RuleFor(x => x.Label).ValidLabel(guard, isNew: false);
        RuleFor(x => x.Color).MaximumLength(20).WithMessage("顏色最多 20 個字元");
    }
}
