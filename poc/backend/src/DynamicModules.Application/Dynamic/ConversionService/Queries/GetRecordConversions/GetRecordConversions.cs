using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;

public record GetRecordConversionsRequest(long RecordId) : IRequest<ServiceResult>;

/// <summary>這張來源記錄可以做的轉換（只列啟用中的定義）、每個來源明細列的已轉量與剩餘量，以及已轉出的目標單據。</summary>
public class GetRecordConversionsHandler(
    IConversionRepository conversions, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, ConversionLedger ledger,
    ConversionValidator validator)
    : IRequestHandler<GetRecordConversionsRequest, ServiceResult>
{
    public const string TargetAddDenied = "沒有目標模組的新增權限";
    public const string QuantityHidden = "沒有數量欄位的檢視權限";

    public async Task<ServiceResult> Handle(GetRecordConversionsRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.RecordId, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var source = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!source.IsReadable() || ModuleRelations.IsDetail(source)) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var sourceValues = RecordValueProcessor.Parse(record.ValuesJson);
        var result = new List<ConversionAvailability>();
        foreach (var conversion in (await conversions.ListBySourceAsync(source.Id, ct)).Where(c => c.Active))
        {
            var target = await modules.GetByIdAsync(conversion.TargetModuleId, includeChildren: true, ct);
            if (!target.IsReadable()) continue;

            string? reason = source.IsWritable() && target.IsWritable() ? null : ModuleGate.ClosedMessage;
            var def = conversion.Definition;
            // 定義已失效（追溯欄位被停用、被帶值佔用…）：不能轉，也不去算剩餘量（欄位可能已不在）
            var valid = await validator.IsDefinitionValidAsync(source, target, def, ct);
            if (reason is null && !valid) reason = Commands.ConvertRecord.ConvertRecordHandler.DefinitionInvalid;
            if (reason is null && def.SourceStates.Count > 0 && (source.Workflow?.StateOf(sourceValues) is not { } state || !def.SourceStates.Contains(state)))
                reason = Commands.ConvertRecord.ConvertRecordHandler.SourceStateMessage;
            var add = await guard.AuthorizeAsync(target, PermissionAction.Add, ct);
            if (reason is null && add.Permissions is null) reason = TargetAddDenied;

            List<ConversionLineStatus>? lines = null;
            if (def.Lines is { } cfg && valid)
            {
                (lines, var hidden) = await LinesAsync(conversion, source, target, record, ct);
                if (reason is null && hidden) reason = QuantityHidden;
            }
            else if (def.Lines is null && reason is null && (await ledger.TargetsOfAsync(conversion, record.Id, ct)).Count > 0)
                reason = Commands.ConvertRecord.ConvertRecordHandler.AlreadyConverted;

            var targetRead = await guard.AuthorizeAsync(target, PermissionAction.Read, ct);
            var targets = new List<ConvertedTarget>();
            if (targetRead.Permissions is { } tp)
                foreach (var t in await ledger.TargetsOfAsync(conversion, record.Id, ct))
                    if (tp.OwnerUserIds is null || tp.OwnerUserIds.Contains(t.OwnerUserId))
                        targets.Add(new ConvertedTarget(t.Id, target.TitleVisibleTo(tp) ? t.Title : "", target.Key));

            result.Add(new ConversionAvailability(conversion.Key, conversion.NameI18n, target.Key, target.NameI18n,
                reason is null, reason, lines, targets));
        }
        return ServiceResult.Success(result);
    }

    private async Task<(List<ConversionLineStatus>? Lines, bool Hidden)> LinesAsync(
        Conversion conversion, Module source, Module target, Record sourceRecord, CancellationToken ct)
    {
        var cfg = conversion.Definition.Lines!;
        var sourceLine = await modules.GetByKeyAsync(cfg.SourceModuleKey, includeChildren: true, ct);
        var targetLine = await modules.GetByKeyAsync(cfg.TargetModuleKey, includeChildren: true, ct);
        if (!sourceLine.IsReadable() || !targetLine.IsReadable()) return (null, false);
        var access = await guard.AuthorizeDetailAsync(source, sourceLine, PermissionAction.Read, ct);
        var qtyField = sourceLine.Blocks.SelectMany(b => b.Fields).First(f => f.Id == cfg.Quantity.From);
        if (access.Permissions is not { } p || !p.CanSee(qtyField)) return (null, true);

        var converted = await ledger.ConvertedAsync(conversion, targetLine, sourceRecord.Id, ct);
        var parentField = ModuleRelations.DetailFieldOf(sourceLine)!;
        var rows = (await records.ListByRefAsync(parentField.Id, sourceRecord.Id.ToString(), ct)).OrderBy(r => r.Id);
        var list = new List<ConversionLineStatus>();
        foreach (var row in rows)
        {
            var qty = ConversionLedger.NumberOf(RecordValueProcessor.Parse(row.ValuesJson), qtyField.Key) ?? 0;
            var done = converted.GetValueOrDefault(row.Id);
            list.Add(new ConversionLineStatus(row.Id, sourceLine.TitleVisibleTo(p) ? row.Title : "", qty, done, qty - done));
        }
        return (list, false);
    }
}
