using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public const int MinLength = 8;

    public ChangePasswordValidator()
    {
        RuleFor(x => x.OldPassword).NotEmpty().WithText(MessageText.OldPasswordRequired);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(MinLength).WithText(MessageText.NewPasswordTooShort, MinLength);
    }
}
