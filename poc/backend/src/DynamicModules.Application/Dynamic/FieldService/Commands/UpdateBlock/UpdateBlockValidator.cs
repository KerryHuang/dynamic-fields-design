using DynamicModules.Application.Dynamic.FieldService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;

public class UpdateBlockValidator : AbstractValidator<UpdateBlockRequest>
{
    public UpdateBlockValidator() => RuleFor(x => x.Label).ValidLabel();
}
