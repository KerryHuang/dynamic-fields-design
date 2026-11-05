using FluentValidation;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

public class ValidationBehavior<TReq, TRes>(IEnumerable<IValidator<TReq>> validators) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        var context = new ValidationContext<TReq>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators)
            failures.AddRange((await v.ValidateAsync(context, ct)).Errors);

        if (failures.Count == 0) return await next();

        if (typeof(TRes) != typeof(ServiceResult)) throw new ValidationException(failures);

        var errors = failures
            .GroupBy(f => f.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray());
        return (TRes)(object)ServiceResult.Invalid(errors);
    }
}
