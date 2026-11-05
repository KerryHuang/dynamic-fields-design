using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;

public class ReorderFieldsValidator : AbstractValidator<ReorderFieldsRequest>
{
    public ReorderFieldsValidator() => RuleFor(x => x.FieldIds).NotNull().WithMessage("欄位清單與區塊內欄位不一致");
}
