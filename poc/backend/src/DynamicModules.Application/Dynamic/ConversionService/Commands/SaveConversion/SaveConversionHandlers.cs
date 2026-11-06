using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;

public class CreateConversionHandler(
    IConversionRepository conversions, IModuleRepository modules, ConversionValidator validator, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time) : IRequestHandler<CreateConversionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateConversionRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;
        if (await conversions.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict("轉換代碼已存在");

        var conversion = new Conversion
        {
            Key = input.Key,
            NameI18n = input.Name,
            SourceModuleId = (await modules.GetByKeyAsync(input.SourceModuleKey, includeChildren: false, ct))!.Id,
            TargetModuleId = (await modules.GetByKeyAsync(input.TargetModuleKey, includeChildren: false, ct))!.Id,
            Active = input.Active,
            Definition = input.Definition
        };
        conversion.StampCreated(user.UserId, time.Now);
        conversions.Add(conversion);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(conversion.ToResponse(input.SourceModuleKey, input.TargetModuleKey));
    }
}

public class UpdateConversionHandler(
    IConversionRepository conversions, ConversionValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateConversionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateConversionRequest request, CancellationToken ct)
    {
        var conversion = await conversions.GetByKeyAsync(request.Key, ct);
        if (conversion is null) return ServiceResult.NotFound("找不到轉換定義");
        var input = request.Input;
        if (input.Key != conversion.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["轉換代碼不能更改"] });
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;

        var modules = await validator.ModulesOfAsync(input, ct);
        if (modules.Source.Id != conversion.SourceModuleId || modules.Target.Id != conversion.TargetModuleId)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["sourceModuleKey"] = ["來源與目標模組不能更改"] });

        conversion.NameI18n = input.Name;
        conversion.Active = input.Active;
        conversion.Definition = input.Definition;
        conversion.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(conversion.ToResponse(modules.Source.Key, modules.Target.Key));
    }
}
