using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>TargetModuleName 帶整份多語系文字，前端依介面語系挑。Title 為 null：看不到這筆記錄的標題欄位。看不到的目標記錄不列出，只計入 HiddenCount。</summary>
public record EffectItem(
    string EventKey, string EffectCode, string Kind, string TargetModuleKey, I18nText TargetModuleName,
    long TargetRecordId, string? Title, DateTime CreatedTime);

public record RecordEffectsResponse(IReadOnlyList<EffectItem> Items, int HiddenCount);

/// <summary>這筆記錄產生了哪些連動記錄（設計 §11.2）。</summary>
public record GetRecordEffectsRequest(long Id) : IRequest<ServiceResult>;

public class GetRecordEffectsHandler(
    IModuleRepository modules, IRecordRepository records, IEventFactRepository facts, ILedgerRepository ledgers, IDynamicAccessGuard guard)
    : IRequestHandler<GetRecordEffectsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordEffectsRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var read = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (read.Permissions is not { } perms) return read.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var items = new List<EffectItem>();
        var hidden = 0;
        var access = new Dictionary<long, (Module Module, EffectivePermissions? Perms)>();
        foreach (var fact in await facts.ListBySourceAsync(record.Id, ct))
        {
            var target = await records.GetAsync(fact.TargetRecordId, ct);
            if (target is null) continue;
            if (!access.TryGetValue(target.ModuleId, out var entry))
            {
                var m = (await modules.GetByIdAsync(target.ModuleId, includeChildren: true, ct))!;
                access[target.ModuleId] = entry = (m, (await guard.AuthorizeAsync(m, PermissionAction.Read, ct)).Permissions);
            }
            if (entry.Perms is not { } p || p.OwnerUserIds is not null && !p.OwnerUserIds.Contains(target.OwnerUserId))
            {
                hidden++;
                continue;
            }
            var kind = await ledgers.FindReversalByAsync(target.Id, ct) is null ? "create" : "reverse";
            items.Add(new EffectItem(fact.EventKey, fact.EffectCode, kind, entry.Module.Key, entry.Module.NameI18n,
                target.Id, entry.Module.TitleVisibleTo(p) ? target.Title : null, fact.CreatedTime));
        }
        return ServiceResult.Success(new RecordEffectsResponse(items, hidden));
    }
}
