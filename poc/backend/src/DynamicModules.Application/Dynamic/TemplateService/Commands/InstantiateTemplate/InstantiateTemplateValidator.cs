using FluentValidation;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;

public class InstantiateTemplateValidator : AbstractValidator<InstantiateTemplateRequest>
{
    public InstantiateTemplateValidator()
    {
        RuleForEach(x => x.Modules).NotNull().WithMessage("模組設定不能空白").ChildRules(m =>
            m.RuleFor(x => x.Code).NotEmpty().WithMessage("模組代號不能空白"));
        RuleFor(x => x.Modules)
            .Must(ms => ms.Where(m => !string.IsNullOrEmpty(m?.Code)).GroupBy(m => m.Code).All(g => g.Count() == 1))
            .WithMessage("模組代號不能重複");
        RuleForEach(x => x.ExcludeFields).NotEmpty().WithMessage("要排除的欄位不能空白");
    }
}
