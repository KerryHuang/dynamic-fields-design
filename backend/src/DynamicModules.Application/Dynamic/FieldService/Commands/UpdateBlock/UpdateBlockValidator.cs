using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Policies;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;

public class UpdateBlockValidator : AbstractValidator<UpdateBlockRequest>
{
    public UpdateBlockValidator(RequiredTextGuard guard)
    {
        RuleFor(x => x.Label).ValidLabel(guard, isNew: false);
        // 技術範圍；生效的上限（form.maxColumns）由 handler 檢查
        var max = PolicyCatalog.FormMaxColumns.Max;
        RuleFor(x => x.Columns).InclusiveBetween(1, max).WithMessage($"每列欄數必須是 1 到 {max}");
    }
}
