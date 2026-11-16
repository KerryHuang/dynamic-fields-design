using DynamicModules.Application.Dynamic.ViewService.Data;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;

public class CreateViewValidator : AbstractValidator<CreateViewRequest>
{
    public CreateViewValidator() => this.ValidateShape(x => x.Name, x => x.Columns, x => x.Sort);
}
