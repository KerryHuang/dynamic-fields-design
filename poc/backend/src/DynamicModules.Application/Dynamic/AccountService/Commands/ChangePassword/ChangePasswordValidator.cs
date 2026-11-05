using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public const int MinLength = 8;

    public ChangePasswordValidator()
    {
        RuleFor(x => x.OldPassword).NotEmpty().WithMessage("請輸入舊密碼");
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(MinLength).WithMessage($"新密碼至少 {MinLength} 碼");
    }
}
