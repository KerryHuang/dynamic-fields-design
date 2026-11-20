using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;

public class AddBlockValidator : AbstractValidator<AddBlockRequest>
{
    public AddBlockValidator(RequiredTextGuard guard) => RuleFor(x => x.Label).ValidLabel(guard, isNew: true);
}
