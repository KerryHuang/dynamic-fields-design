using DynamicModules.Application.Dynamic.ModuleService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;

public class CreateModuleValidator : AbstractValidator<CreateModuleRequest>
{
    public CreateModuleValidator()
    {
        RuleFor(x => x.Key).NotEmpty().WithMessage("模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭").ValidModuleKey();
        RuleFor(x => x.Name).ValidModuleName();
        RuleFor(x => x.Section).MaximumLength(100).WithMessage("選單分區不能超過 100 字");
    }
}
