using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.CheckService;

public class CreateCheckHandler(
    ICheckRepository checks, IModuleRepository modules, CheckValidator validator, CheckIndexer indexer, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateCheckRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;
        if (await checks.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict("檢查代碼已存在");
        var module = (await modules.GetByKeyAsync(input.ModuleKey, includeChildren: true, ct))!;
        var (indexFailure, notices) = await indexer.PrepareAsync(module, input.Definition, ct);
        if (indexFailure is not null) return indexFailure;
        var c = new ModuleCheck { Key = input.Key, NameI18n = input.Name, ModuleId = module.Id, Active = input.Active, Definition = input.Definition };
        c.StampCreated(user.UserId, time.Now);
        checks.Add(c);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(c.ToResponse(module.Key, notices));
    }
}

public class UpdateCheckHandler(
    ICheckRepository checks, IModuleRepository modules, CheckValidator validator, CheckIndexer indexer, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateCheckRequest request, CancellationToken ct)
    {
        var c = await checks.GetByKeyAsync(request.Key, ct);
        if (c is null) return ServiceResult.NotFound("找不到檢查");
        var input = request.Input;
        if (input.Key != c.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["檢查代碼不能更改"] });
        var module = (await modules.GetByIdAsync(c.ModuleId, includeChildren: true, ct))!;
        if (input.ModuleKey != module.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["moduleKey"] = ["模組不能更改"] });
        if (await validator.ValidateAsync(input, ct) is { } invalid) return invalid;
        var (indexFailure, notices) = await indexer.PrepareAsync(module, input.Definition, ct);
        if (indexFailure is not null) return indexFailure;
        c.NameI18n = input.Name;
        c.Active = input.Active;
        c.Definition = input.Definition;
        c.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(c.ToResponse(module.Key, notices));
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

public class ScanCheckHandler(ICheckRepository checks, IModuleRepository modules, IRecordRepository records, CheckEvaluator evaluator)
    : IRequestHandler<ScanCheckRequest, ServiceResult>
{
    public const int MaxViolations = 50;
    public const int MaxGroups = 20_000;

    public async Task<ServiceResult> Handle(ScanCheckRequest request, CancellationToken ct)
    {
        var check = await checks.GetByKeyAsync(request.Key, ct);
        if (check is null) return ServiceResult.NotFound("找不到檢查");
        var module = (await modules.GetByIdAsync(check.ModuleId, includeChildren: true, ct))!;
        var (prepared, failure) = await evaluator.PrepareAsync(module, check, ct);
        if (failure is not null) return failure;

        var seen = new HashSet<string>();
        var violations = new List<CheckViolation>();
        long after = 0;
        while (true)
        {
            var batch = await records.ListBatchByModuleAsync(module.Id, after, CheckIndexer.BatchSize, ct);
            if (batch.Count == 0) break;
            foreach (var record in batch)
            {
                if (evaluator.GroupOf(prepared!, RecordService.RecordValueProcessor.Parse(record.ValuesJson)) is not { } group || !seen.Add(group.Id)) continue;
                var outcome = await evaluator.EvaluateAsync(prepared!, group, ct);
                if (outcome.Failure is not null) return outcome.Failure;
                if (outcome.Violation is not null) violations.Add(new CheckViolation(outcome.Value, outcome.Limit, outcome.Violation));
                if (violations.Count >= MaxViolations || seen.Count >= MaxGroups)
                    return ServiceResult.Success(new CheckScanResponse(violations, Truncated: true));
            }
            records.ReleaseTracked(batch);
            after = batch[^1].Id;
        }
        return ServiceResult.Success(new CheckScanResponse(violations, Truncated: false));
    }
}
