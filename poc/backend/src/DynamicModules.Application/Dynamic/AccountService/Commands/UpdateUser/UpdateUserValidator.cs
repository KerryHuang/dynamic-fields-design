using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.UpdateUser;

public class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().WithText(MessageText.DisplayNameRequired).MaximumLength(100).WithText(MessageText.DisplayNameTooLong, 100);
    }
}
