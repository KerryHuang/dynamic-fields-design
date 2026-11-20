using DynamicModules.Application.Dynamic.ViewService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;

public class CreateViewValidator : AbstractValidator<CreateViewRequest>
{
    /// <summary><paramref name="guard"/> 為 null 是樣板建立檢視：名稱照套件規則要求繁中（判斷 12）。</summary>
    public CreateViewValidator(Common.RequiredTextGuard? guard = null) => this.ValidateShape(x => x.Name, x => x.Columns, x => x.Sort, guard, isNew: true);
}
