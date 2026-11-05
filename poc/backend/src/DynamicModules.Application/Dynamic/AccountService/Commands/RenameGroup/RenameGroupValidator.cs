using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.RenameGroup;

public class RenameGroupValidator : AbstractValidator<RenameGroupRequest>
{
    public RenameGroupValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("請輸入群組名稱").MaximumLength(100).WithMessage("群組名稱最多 100 字");
        RuleFor(x => x.Locale).Must(l => I18nText.SupportedLocales.Contains(l)).WithMessage("不支援的語系");
    }
}
