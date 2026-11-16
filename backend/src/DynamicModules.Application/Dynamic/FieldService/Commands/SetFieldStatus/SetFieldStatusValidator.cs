using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;

public class SetFieldStatusValidator : AbstractValidator<SetFieldStatusRequest>
{
    public SetFieldStatusValidator() => RuleFor(x => x.Status).IsInEnum().WithMessage("欄位狀態不正確");
}
