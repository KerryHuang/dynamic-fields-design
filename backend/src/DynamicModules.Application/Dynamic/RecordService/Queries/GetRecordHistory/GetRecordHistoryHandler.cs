using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;

/// <summary>記錄自己與所有明細列（含已刪除）的歷程（已定決策 3、4）；讀者看不到的欄位整列不回。</summary>
public class GetRecordHistoryHandler(
    IModuleRepository modules, IRecordRepository records, IRecordHistoryRepository histories,
    IPermissionRepository permissions, IDynamicAccessGuard guard, ReferenceResolver resolver)
    : IRequestHandler<GetRecordHistoryRequest, ServiceResult>
{
    private const int DefaultPagesSize = 20;

    /// <summary>一組歷程的來源：表頭或某個明細模組，各自用自己的權限過濾與解析欄位。</summary>
    private sealed record Source(Module Module, EffectivePermissions Perms, bool IsDetail);

    public async Task<ServiceResult> Handle(GetRecordHistoryRequest request, CancellationToken ct)
    {
        // 404 與範圍檢查照 GET /records/{id}
        var record = await records.GetAsync(request.Id, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable()) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;
        if (p.OwnerUserIds is not null && !p.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        // 記錄 Id → 來源；明細列以索引找，不看 DeleteFlag
        var sourceOf = new Dictionary<long, Source> { [record.Id] = new(module, p, false) };
        foreach (var child in await DetailWriter.ReadableDetailModulesAsync(modules, module, ct))
        {
            var childAccess = await guard.AuthorizeDetailAsync(module, child, PermissionAction.Read, ct);
            if (childAccess.Permissions is not { } cp) continue;
            var detailField = ModuleRelations.DetailFieldOf(child)!;
            var source = new Source(child, cp, true);
            foreach (var id in await records.ListDetailRowIdsAsync(detailField.Id, record.Id.ToString(), ct))
                sourceOf[id] = source;
        }
        var sources = sourceOf.Values.Distinct().ToList();
        var hidden = sources.SelectMany(s => s.Module.Blocks.SelectMany(b => b.Fields).Where(f => !s.Perms.CanSee(f)))
            .Select(f => f.Id).ToHashSet();

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var size = request.PagesSize <= 0 ? DefaultPagesSize : Math.Min(request.PagesSize, PagedRequest.MaxPagesSize);
        var (items, total) = await histories.QueryAsync(sourceOf.Keys, hidden, page, size, ct);

        var locale = string.IsNullOrEmpty(request.Locale) ? "zh-TW" : request.Locale;
        var userNames = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var fieldsById = sources.SelectMany(s => s.Module.Blocks.SelectMany(b => b.Fields)).ToDictionary(f => f.Id);
        var (oldRefs, newRefs) = await ResolveReferencesAsync(items, sourceOf, fieldsById, ct);
        var rowTitles = await DetailRowTitlesAsync(items, sourceOf, ct);

        var result = items.Select(h =>
        {
            var source = sourceOf[h.RecordId];
            var field = h.FieldId is { } fid ? fieldsById.GetValueOrDefault(fid) : null;
            string? Text(string? raw, IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>> refs) =>
                field is null ? null
                    : FieldDisplay.Format(field, Parse(raw), locale,
                        refs.GetValueOrDefault(h.Id)?.GetValueOrDefault(field.Key), userNames);
            var detail = source.IsDetail
                ? new HistoryDetailRow(source.Module.Key, source.Module.NameI18n, h.RecordId,
                    source.Module.TitleVisibleTo(source.Perms) ? rowTitles.GetValueOrDefault(h.RecordId, "") : "")
                : null;
            return new RecordHistoryResponse(h.Id, h.Operation.ToString().ToLowerInvariant(), field?.Key, field?.LabelI18n,
                Text(h.OldValue, oldRefs), Text(h.NewValue, newRefs), h.ChangedUserId,
                userNames.GetValueOrDefault(h.ChangedUserId, h.ChangedUserId), h.ChangedTime, detail);
        }).ToList();
        return ServiceResult.Success(new PagedResponse<RecordHistoryResponse>(result, total, page, size));
    }

    private static JsonElement? Parse(string? raw) =>
        raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>
    /// 參照欄位的舊值、新值依讀者目前的權限解析標題：組成暫時的 Record（Id 用歷程列的 Id、ValuesJson 只放該欄位）
    /// 交給 ReferenceResolver，依來源模組分組；回傳的鍵是歷程列 Id。
    /// </summary>
    private async Task<(IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>> Old,
        IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>> New)> ResolveReferencesAsync(
        IReadOnlyList<RecordHistory> items, Dictionary<long, Source> sourceOf, Dictionary<long, Field> fieldsById, CancellationToken ct)
    {
        var oldRefs = new Dictionary<long, Dictionary<string, ReferenceInfo>>();
        var newRefs = new Dictionary<long, Dictionary<string, ReferenceInfo>>();
        var refRows = items.Where(h => h.FieldId is { } fid && fieldsById.TryGetValue(fid, out var f) && f.Type == FieldType.Reference);
        foreach (var group in refRows.GroupBy(h => sourceOf[h.RecordId]))
        {
            Record Temp(RecordHistory h, string? raw) => new()
            {
                Id = h.Id,
                ValuesJson = raw is null ? "{}" : $"{{{JsonSerializer.Serialize(fieldsById[h.FieldId!.Value].Key)}:{raw}}}"
            };
            foreach (var kv in await resolver.ResolveAsync(group.Key.Module, group.Key.Perms, group.Select(h => Temp(h, h.OldValue)).ToList(), ct))
                oldRefs[kv.Key] = kv.Value;
            foreach (var kv in await resolver.ResolveAsync(group.Key.Module, group.Key.Perms, group.Select(h => Temp(h, h.NewValue)).ToList(), ct))
                newRefs[kv.Key] = kv.Value;
        }
        return (oldRefs, newRefs);
    }

    /// <summary>本頁出現的明細列標題（含已刪除的列）。</summary>
    private async Task<Dictionary<long, string>> DetailRowTitlesAsync(
        IReadOnlyList<RecordHistory> items, Dictionary<long, Source> sourceOf, CancellationToken ct)
    {
        var titles = new Dictionary<long, string>();
        foreach (var group in items.Select(h => h.RecordId).Distinct().Where(id => sourceOf[id].IsDetail).GroupBy(id => sourceOf[id].Module))
        {
            var ids = group.ToHashSet();
            // POC：沒有「依 Id 取含已刪除記錄」的查詢，先用模組全載再過濾；上線時改成依 Id 查
            foreach (var row in await records.ListAllByModuleAsync(group.Key.Id, ct))
                if (ids.Contains(row.Id)) titles[row.Id] = row.Title;
        }
        return titles;
    }
}
