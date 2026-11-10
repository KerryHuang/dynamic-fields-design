using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Rules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

public class UpdateRecordHandler(
    IModuleRepository modules, IRecordRepository records, FieldTypeRegistry registry,
    ICurrentUser user, IBusinessTime time, IDynamicAccessGuard guard, ReferenceValidator refs, ReferenceFill fill, ReferenceResolver resolver,
    DetailWriter details, DetailReader detailReader, RollupRecalc rollups, RecordHistoryWriter history, WorkflowEnforcer workflow,
    Dynamic.LedgerService.LedgerGate ledgerGate, Dynamic.EventService.EventFactLock factLock, Dynamic.ApprovalService.ApprovalGate approvalGate)
    : IRequestHandler<UpdateRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateRecordRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return blocked;
        var access = await guard.AuthorizeAsync(module, PermissionAction.Edit, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (await ledgerGate.RejectWriteAsync(module, ct) is { } ledgerLocked) return ledgerLocked;
        if (await approvalGate.RejectAsync(module, record, RecordValueProcessor.Parse(record.ValuesJson), request.Values, false, ct,
                request.Details?.Values.Any(rows => rows.Count > 0) == true) is { } approvalBlocked) return approvalBlocked;
        var frozen = await factLock.IsFrozenAsync(record.Id, ct);
        if (frozen && factLock.RejectEdit(module, request.Values.Keys, request.Details?.Values.Any(rows => rows.Count > 0) == true) is { } factLocked) return factLocked;
        if (record.RowVersion != request.RowVersion) return ServiceResult.Conflict(RecordMapper.Stale);

        var userIds = await modules.ListUserIdsAsync(ct);
        var previous = RecordValueProcessor.Parse(record.ValuesJson);
        // 決策 4 第 2 步：表頭被全單唯讀鎖住時整個請求擋下，只改明細也一樣
        if (RuleEngine.Evaluate(module, previous, previous, registry, userIds, time.Today).ReadOnlyAll)
            return ServiceResult.Invalid(new Dictionary<string, string[]>
            {
                [RecordValueProcessor.RecordErrorKey] = [RecordValueProcessor.ReadOnlyRecordMessage]
            });

        // 狀態機的唯讀狀態：除了狀態欄位，明細也不能動（表頭欄位由 Process 忽略）
        if (module.Workflow is { } wf && wf.IsLocked(previous) && request.Details?.Values.Any(rows => rows.Count > 0) == true)
            return ServiceResult.Invalid(new Dictionary<string, string[]>
            {
                [RecordValueProcessor.RecordErrorKey] = [WorkflowEnforcer.LockedDetailsMessage]
            });

        var validRefKeys = await refs.ValidKeysAsync(module, request.Values, previous, ct);
        RecordValueProcessor.RejectFrozenInput(module, request.Values, previous, userIds, registry, perms, validRefKeys, request.SystemKeys);
        var input = new Dictionary<string, JsonElement?>(request.Values);
        var systemKeys = new HashSet<string>(await fill.ApplyAsync(module, input, previous, validRefKeys,
            RecordValueProcessor.WritablePredicate(module, previous, perms, registry, userIds, time.Today), ct));
        if (request.SystemKeys is not null) systemKeys.UnionWith(request.SystemKeys);
        WorkflowEnforcer.SeedMissingState(module, previous, input, systemKeys);
        var plan = await details.PrepareAsync(module, record, PermissionAction.Edit, request.Details, ct,
            () => Task.FromResult<IReadOnlyDictionary<string, JsonElement?>>(RecordValueProcessor.Process(
                module, input, previous, userIds, registry, perms, new RuleContext(time.Today), validRefKeys, systemKeys: systemKeys).values));
        if (plan.Failure is { } failure) return failure;
        var (values, errors) = RecordValueProcessor.Process(
            module, input, previous, userIds, registry, perms, new RuleContext(time.Today), validRefKeys, plan.RollupValues, systemKeys: systemKeys, frozen: frozen);
        foreach (var (key, messages) in plan.Errors) errors[key] = messages;
        if (errors.Count == 0)
            foreach (var (key, messages) in await workflow.CheckAsync(module, previous, values, ct)) errors[key] = messages;
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        FieldLocks.Restore(module, perms.Frozen, previous, values);   // 生命週期鎖第二道：凍結欄位落地時蓋回舊值
        if (module.Kind != ModuleKind.Extension) record.Title = RecordValueProcessor.TitleOf(module, values);
        record.ValuesJson = RecordValueProcessor.Serialize(values);
        history.Updated(record, module, previous, values);
        record.RowVersion = Guid.NewGuid().ToString();
        record.StampModified(user.UserId, time.Now);
        await records.ReplaceIndexAsync(record, RecordIndexer.Build(module, values, registry), ct);
        await details.ApplyAsync(plan, record, ct);
        await rollups.AfterHeaderSaveAsync(module, record, previous, values, plan, ct);
        var shown = await resolver.ResolveAsync(module, perms, [record], ct);
        return ServiceResult.Success(record.ToResponse(module.Key, perms, module, shown.GetValueOrDefault(record.Id))
            with { Details = await detailReader.ReadAsync(module, record, PermissionAction.Edit, ct) });
    }
}
