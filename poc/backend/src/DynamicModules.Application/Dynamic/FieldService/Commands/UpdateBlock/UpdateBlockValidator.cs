using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;

public class UpdateBlockValidator : AbstractValidator<UpdateBlockRequest>
{
    public UpdateBlockValidator()
    {
        RuleFor(x => x.Label).ValidLabel();
        RuleFor(x => x.Columns).InclusiveBetween(1, Block.MaxColumns).WithMessage($"每列欄數必須是 1 到 {Block.MaxColumns}");
    }
}
