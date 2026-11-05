using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MinimumLength(ChangePasswordValidator.MinLength)
            .WithMessage($"密碼至少 {ChangePasswordValidator.MinLength} 碼");
    }
}
