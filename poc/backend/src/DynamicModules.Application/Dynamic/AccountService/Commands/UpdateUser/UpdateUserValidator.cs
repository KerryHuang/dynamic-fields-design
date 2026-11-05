using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.UpdateUser;

public class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().WithMessage("請輸入姓名").MaximumLength(100).WithMessage("姓名最多 100 字");
    }
}
