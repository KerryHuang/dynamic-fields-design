using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.EventService;

public class CreateEventHandler(
    IEventRepository events, IModuleRepository modules, EventValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateEventRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateEventRequest request, CancellationToken ct)
    {
        var input = request.Input;
        var (invalid, warnings) = await validator.ValidateCoreAsync(input, ct);
        if (invalid is not null) return invalid;
        if (await events.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict("事件代碼已存在");

        var source = (await modules.GetByKeyAsync(input.SourceModuleKey, includeChildren: false, ct))!;
        var e = new ModuleEvent
        {
            Key = input.Key, NameI18n = input.Name, SourceModuleId = source.Id, Active = input.Active, Definition = input.Definition
        };
        e.StampCreated(user.UserId, time.Now);
        events.Add(e);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(e.ToResponse(source.Key, warnings));
    }
}

public class UpdateEventHandler(
    IEventRepository events, IModuleRepository modules, EventValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateEventRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateEventRequest request, CancellationToken ct)
    {
        var e = await events.GetByKeyAsync(request.Key, ct);
        if (e is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.EventNotFound));
        var input = request.Input;
        if (input.Key != e.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["事件代碼不能更改"] });
        var source = (await modules.GetByIdAsync(e.SourceModuleId, includeChildren: false, ct))!;
        if (input.SourceModuleKey != source.Key)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["sourceModuleKey"] = ["來源模組不能更改"] });
        var (invalid, warnings) = await validator.ValidateCoreAsync(input, ct);
        if (invalid is not null) return invalid;

        e.NameI18n = input.Name;
        e.Active = input.Active;
        e.Definition = input.Definition;
        e.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(e.ToResponse(source.Key, warnings));
    }
}

public class DeleteEventHandler(IEventRepository events, IUnitOfWork uow) : IRequestHandler<DeleteEventRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteEventRequest request, CancellationToken ct)
    {
        var e = await events.GetByKeyAsync(request.Key, ct);
        if (e is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.EventNotFound));
        var user = (await events.ListAsync(ct)).FirstOrDefault(o => o.Key != e.Key && o.Definition.Effects.Any(x => x is { Kind: "reverse" } && x.OfEvent == e.Key));
        if (user is not null) return ServiceResult.Conflict($"事件「{user.Key}」的沖銷效果引用了這個事件，請先移除該效果");
        events.Remove(e);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetEventsHandler(IEventRepository events, IModuleRepository modules) : IRequestHandler<GetEventsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetEventsRequest request, CancellationToken ct)
    {
        List<ModuleEvent> list;
        if (request.Key is not null)
        {
            var one = await events.GetByKeyAsync(request.Key, ct);
            if (one is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.EventNotFound));
            list = [one];
        }
        else if (request.SourceModuleKey is not null)
        {
            var source = await modules.GetByKeyAsync(request.SourceModuleKey, includeChildren: false, ct);
            list = source is null ? [] : await events.ListBySourceAsync(source.Id, ct);
        }
        else list = await events.ListAsync(ct);

        var keys = new Dictionary<long, string>();
        foreach (var id in list.Select(e => e.SourceModuleId).Distinct())
            if (await modules.GetByIdAsync(id, includeChildren: false, ct) is { } m) keys[id] = m.Key;
        var items = list.Select(e => e.ToResponse(keys.GetValueOrDefault(e.SourceModuleId, ""))).ToList();
        return ServiceResult.Success(request.Key is not null ? items[0] : (IReadOnlyList<EventResponse>)items);
    }
}
