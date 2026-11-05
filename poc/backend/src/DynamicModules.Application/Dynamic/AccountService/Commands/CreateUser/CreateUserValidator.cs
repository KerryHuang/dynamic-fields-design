using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateUser;

public class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    /// <summary>帳號存小寫；比對前先轉小寫，所以輸入 Ann.Lee 合法，含空白或中文不合法。</summary>
    public static readonly System.Text.RegularExpressions.Regex IdPattern = new("^[a-z0-9._-]{2,50}$");

    public CreateUserValidator()
    {
        RuleFor(x => x.UserId).Must(id => id is not null && IdPattern.IsMatch(id.ToLowerInvariant()))
            .WithMessage("帳號只能用英數字、點、底線、減號，長度 2 到 50");
        RuleFor(x => x.DisplayName).NotEmpty().WithMessage("請輸入姓名").MaximumLength(100).WithMessage("姓名最多 100 字");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(ChangePasswordValidator.MinLength)
            .WithMessage($"密碼至少 {ChangePasswordValidator.MinLength} 碼");
    }
}
