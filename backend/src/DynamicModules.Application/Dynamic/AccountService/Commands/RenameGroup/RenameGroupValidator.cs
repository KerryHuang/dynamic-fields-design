using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.RenameGroup;

public class RenameGroupValidator : AbstractValidator<RenameGroupRequest>
{
    public RenameGroupValidator()
    {
        // 語系不在這裡擋：沒給、不認得或停用都由 handler 的 RequestLocale.For 決定（D4）
        RuleFor(x => x.Name).NotEmpty().WithMessage("請輸入群組名稱").MaximumLength(100).WithMessage("群組名稱最多 100 字");
    }
}
