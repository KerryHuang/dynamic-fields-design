using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateUser;

public class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    /// <summary>帳號存小寫；比對前先轉小寫，所以輸入 Ann.Lee 合法，含空白或中文不合法。</summary>
    public static readonly System.Text.RegularExpressions.Regex IdPattern = new(@"^[a-z0-9._-]{2,50}\z");

    public CreateUserValidator()
    {
        RuleFor(x => x.UserId).Must(id => id is not null && IdPattern.IsMatch(id.ToLowerInvariant()))
            .WithText(MessageText.UserIdFormat);
        RuleFor(x => x.DisplayName).NotEmpty().WithText(MessageText.DisplayNameRequired).MaximumLength(100).WithText(MessageText.DisplayNameTooLong, 100);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(ChangePasswordValidator.MinLength)
            .WithText(MessageText.PasswordTooShort, ChangePasswordValidator.MinLength);
    }
}
