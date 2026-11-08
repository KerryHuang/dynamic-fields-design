using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.CheckService;

public class CreateCheckHandler(
    ICheckRepository checks, IModuleRepository modules, CheckValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateCheckRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;
        if (await checks.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict("檢查代碼已存在");
        var module = (await modules.GetByKeyAsync(input.ModuleKey, includeChildren: false, ct))!;
        var c = new ModuleCheck { Key = input.Key, NameI18n = input.Name, ModuleId = module.Id, Active = input.Active, Definition = input.Definition };
        c.StampCreated(user.UserId, time.Now);
        checks.Add(c);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(c.ToResponse(module.Key));
    }
}

public class UpdateCheckHandler(
    ICheckRepository checks, IModuleRepository modules, CheckValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateCheckRequest request, CancellationToken ct)
    {
        var c = await checks.GetByKeyAsync(request.Key, ct);
        if (c is null) return ServiceResult.NotFound("找不到檢查");
        var input = request.Input;
        if (input.Key != c.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["檢查代碼不能更改"] });
        var module = (await modules.GetByIdAsync(c.ModuleId, includeChildren: false, ct))!;
        if (input.ModuleKey != module.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["moduleKey"] = ["模組不能更改"] });
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;
        c.NameI18n = input.Name;
        c.Active = input.Active;
        c.Definition = input.Definition;
        c.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(c.ToResponse(module.Key));
    }
}

public class DeleteCheckHandler(ICheckRepository checks, IUnitOfWork uow) : IRequestHandler<DeleteCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteCheckRequest request, CancellationToken ct)
    {
        var c = await checks.GetByKeyAsync(request.Key, ct);
        if (c is null) return ServiceResult.NotFound("找不到檢查");
        checks.Remove(c);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetChecksHandler(ICheckRepository checks, IModuleRepository modules) : IRequestHandler<GetChecksRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetChecksRequest request, CancellationToken ct)
    {
        List<ModuleCheck> list;
        if (request.Key is not null)
        {
            var one = await checks.GetByKeyAsync(request.Key, ct);
            if (one is null) return ServiceResult.NotFound("找不到檢查");
            list = [one];
        }
        else if (request.ModuleKey is not null)
        {
            var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
            list = module is null ? [] : await checks.ListByModuleAsync(module.Id, ct);
        }
        else list = await checks.ListAsync(ct);

        var keys = new Dictionary<long, string>();
        foreach (var id in list.Select(c => c.ModuleId).Distinct())
            if (await modules.GetByIdAsync(id, includeChildren: false, ct) is { } m) keys[id] = m.Key;
        var items = list.Select(c => c.ToResponse(keys.GetValueOrDefault(c.ModuleId, ""))).ToList();
        return ServiceResult.Success(request.Key is not null ? items[0] : (IReadOnlyList<CheckResponse>)items);
    }
}
