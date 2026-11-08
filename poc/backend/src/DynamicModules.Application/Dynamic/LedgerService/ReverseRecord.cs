using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.LedgerService;

/// <summary>沖銷一筆分錄：新增一筆鏡像分錄（設計 §6.3）。Values 只能覆寫說明性欄位。</summary>
public record ReverseRecordRequest(long Id, Dictionary<string, JsonElement?>? Values) : IRequest<ServiceResult>, IRecordWriteRequest;

public record LedgerLink(long Id, string Title);

/// <summary>Reversible：這筆能不能被目前使用者沖銷（分錄、不是沖銷分錄、尚未被沖銷、有 Add 與 Read 權限）。</summary>
public record RecordLedgerResponse(bool Reversible, LedgerLink? ReversedBy, LedgerLink? Reverses);

public record GetRecordLedgerRequest(long Id) : IRequest<ServiceResult>;

public class ReverseRecordHandler(
    ILedgerRepository ledgers, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    LedgerReverser reverser, IUnitOfWork uow, IBusinessTime time, IRequestHandler<CreateRecordRequest, ServiceResult> create)
    : IRequestHandler<ReverseRecordRequest, ServiceResult>
{
    public const string NotLedger = "這個模組不是分錄，不能沖銷";
    public const string AlreadyReversed = "這筆分錄已經沖銷過了";
    public const string IsReversal = "沖銷分錄本身不能再被沖銷；要重新入帳請新增一筆";

    public async Task<ServiceResult> Handle(ReverseRecordRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null || record.DeleteFlag) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var ledger = await ledgers.GetByModuleAsync(module.Id, ct);
        if (ledger is null) return ServiceResult.Conflict(NotLedger);
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return blocked;

        var read = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (read.Permissions is not { } perms) return read.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var add = await guard.AuthorizeAsync(module, PermissionAction.Add, ct);
        if (add.Permissions is null) return add.Denied!;

        var values = RecordValueProcessor.Parse(record.ValuesJson);
        var reversalKey = $"f_{ledger.Definition.ReversalFieldId}";
        if (values.TryGetValue(reversalKey, out var rv) && rv is { ValueKind: not JsonValueKind.Null }
            || await ledgers.FindReversalByAsync(record.Id, ct) is not null)
            return ServiceResult.Conflict(IsReversal);
        if (await ledgers.FindReversalOfAsync(record.Id, ct) is not null) return ServiceResult.Conflict(AlreadyReversed);

        var plan = await reverser.PlanAsync(module, ledger, record, perms, request.Values, ct);
        if (plan.Conflict is not null) return ServiceResult.Conflict(plan.Conflict);
        if (plan.Errors.Count > 0) return ServiceResult.Invalid(plan.Errors);

        var created = await create.Handle(
            // 鏡射值（含沖銷欄位）都以系統填入處理：使用者看得到才會進計畫；欄位對他唯讀時，一般新增會忽略該欄位而套預設值，
            // 沖銷就不再是精確抵銷。使用者覆寫的說明性欄位已在計畫階段檢查過寫入權限。
            new CreateRecordRequest(module.Key, plan.Values, null, plan.Details.Count > 0 ? plan.Details : null, plan.Values.Keys.ToHashSet()), ct);
        if (!created.IsSuccess) return created;

        ledgers.AddReversal(new LedgerReversal
        {
            OriginalRecordId = record.Id, ReversalRecordId = ((DynamicRecordResponse)created.Data!).Id, CreatedTime = time.Now
        });
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ServiceResult.Conflict(AlreadyReversed);
        }
        return created;
    }
}

public class GetRecordLedgerHandler(
    ILedgerRepository ledgers, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard)
    : IRequestHandler<GetRecordLedgerRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordLedgerRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null || record.DeleteFlag) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var read = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (read.Permissions is not { } perms) return read.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var ledger = await ledgers.GetByModuleAsync(module.Id, ct);
        if (ledger is null) return ServiceResult.Success(new RecordLedgerResponse(false, null, null));

        var titleVisible = module.TitleFieldId is not { } tid
                           || module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == tid) is not { } tf || perms.CanSee(tf);
        async Task<LedgerLink?> Link(long? id)
        {
            if (id is not { } rid || await records.GetAsync(rid, ct) is not { DeleteFlag: false } r) return null;
            if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(r.OwnerUserId)) return null;
            return new LedgerLink(r.Id, titleVisible ? r.Title : "");
        }

        var reversedBy = await ledgers.FindReversalOfAsync(record.Id, ct);
        var reverses = await ledgers.FindReversalByAsync(record.Id, ct);
        var canAdd = (await guard.AuthorizeAsync(module, PermissionAction.Add, ct)).Permissions is not null;
        return ServiceResult.Success(new RecordLedgerResponse(
            canAdd && reversedBy is null && reverses is null && module.IsWritable(),
            await Link(reversedBy?.ReversalRecordId), await Link(reverses?.OriginalRecordId)));
    }
}
