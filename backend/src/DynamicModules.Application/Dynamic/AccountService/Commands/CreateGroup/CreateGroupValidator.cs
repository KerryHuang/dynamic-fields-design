using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;

public class CreateGroupValidator : AbstractValidator<CreateGroupRequest>
{
    public CreateGroupValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("請輸入群組名稱").MaximumLength(100).WithMessage("群組名稱最多 100 字");
        // 沒給語系由 handler 用請求語系（D4）；給了就要是認得的代碼
        RuleFor(x => x.Locale).Must(l => string.IsNullOrWhiteSpace(l) || I18nText.SupportedLocales.Contains(l)).WithMessage("不支援的語系");
    }
}
