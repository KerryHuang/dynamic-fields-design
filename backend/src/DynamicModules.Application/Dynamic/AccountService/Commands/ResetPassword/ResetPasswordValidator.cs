using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithText(MessageText.PasswordTooShort, ChangePasswordValidator.MinLength)
            .MinimumLength(ChangePasswordValidator.MinLength).WithText(MessageText.PasswordTooShort, ChangePasswordValidator.MinLength);
    }
}
