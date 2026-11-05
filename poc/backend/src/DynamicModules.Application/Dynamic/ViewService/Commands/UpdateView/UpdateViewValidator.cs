using DynamicModules.Application.Dynamic.ViewService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;

public class UpdateViewValidator : AbstractValidator<UpdateViewRequest>
{
    public UpdateViewValidator() => this.ValidateShape(x => x.Name, x => x.Columns, x => x.Sort);
}
