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
        // NotEmpty 與 MinimumLength 各自帶訊息：只在最後一條上 WithText 的話，空字串會落到 FluentValidation 的內建英文
        RuleFor(x => x.NewPassword).NotEmpty().WithText(MessageText.NewPasswordTooShort, MinLength)
            .MinimumLength(MinLength).WithText(MessageText.NewPasswordTooShort, MinLength);
    }
}
