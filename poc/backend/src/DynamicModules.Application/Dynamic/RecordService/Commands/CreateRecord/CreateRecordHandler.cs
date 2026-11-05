using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;

public class CreateRecordHandler(
    IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, IDynamicAccessGuard guard, ReferenceResolver resolver,
    ReferenceProviderRegistry providers, DetailWriter details, DetailReader detailReader, RollupRecalc rollups, RecordCreator creator)
    : IRequestHandler<CreateRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateRecordRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is not { Status: ModuleStatus.Active } || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Add, ct);
        if (access.Permissions is not { } perms) return access.Denied!;

        string? hostKey = null, hostTitle = null;
        if (module.Kind == ModuleKind.Extension)
        {
            hostKey = request.HostKey;
            if (string.IsNullOrEmpty(hostKey)) return HostInvalid("擴充模組的記錄必須指定既有資料");
            var titles = await providers.Get(module.HostEntityKey!)!.TitlesAsync([hostKey], ct);
            if (!titles.TryGetValue(hostKey, out hostTitle)) return HostInvalid("既有資料不存在");
            if (await records.GetByHostAsync(module.Id, hostKey, ct) is not null) return HostConflict;
        }

        // 決策 4：先處理明細列並算出彙總，表頭的公式與規則才看得到新值；全部錯誤收齊後才取號、寫入
        var plan = await details.PrepareAsync(module, null, PermissionAction.Add, request.Details, ct,
            async () => (await creator.ValidateAsync(module, perms, request.Values, null, ct)).Values);
        if (plan.Failure is { } failure) return failure;
        // 決策 7：明細彙總已依這次的列算好；其他彙總（一般參照）新記錄還沒有子記錄，由 RecordCreator 取空集合的值
        var (values, errors) = await creator.ValidateAsync(module, perms, request.Values, plan.RollupValues, ct);
        foreach (var (key, messages) in plan.Errors) errors[key] = messages;
        if (errors.Count > 0) return ServiceResult.Invalid(errors);
        var record = await creator.InsertAsync(module, values, hostKey, hostTitle, ct);
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (DbUpdateException) when (hostKey is not null)
        {
            // 查重與寫入之間被別人搶先：唯一索引兜底
            return HostConflict;
        }
        if (plan.Sets.Any(x => x.Changes.Count > 0))
        {
            // 表頭先提交取得 Id，明細列才能指向它；失敗時 TransactionBehavior 一起回滾
            await details.ApplyAsync(plan, record, ct);
        }
        await rollups.AfterHeaderSaveAsync(module, record, null, values, plan, ct);
        var shown = await resolver.ResolveAsync(module, perms, [record], ct);
        return ServiceResult.Created(record.ToResponse(module.Key, perms, module, shown.GetValueOrDefault(record.Id))
            with { Details = await detailReader.ReadAsync(module, record, PermissionAction.Add, ct) });
    }

    private static readonly ServiceResult HostConflict = ServiceResult.Conflict("這筆既有資料已經有擴充資料");

    private static ServiceResult HostInvalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["hostKey"] = [message] });
}
