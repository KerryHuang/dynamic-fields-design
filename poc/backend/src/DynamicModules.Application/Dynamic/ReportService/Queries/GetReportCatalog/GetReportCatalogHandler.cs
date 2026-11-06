using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

/// <summary>停用的選項也列出（Active = false），已存的篩選仍顯示得出來；前端負責標示並禁止新選。</summary>
public record CatalogOption(string Id, string Label, bool Active);

public record CatalogField(
    long Id, string Label, FieldType Type, bool CanGroup, bool CanSum, bool CanMinMax, bool IsDate, List<CatalogOption>? Options,
    string? RefKind = null, string? RefKey = null);

public record CatalogVia(long FieldId, string Label, string TargetKey, List<CatalogField> Fields);

public record CatalogChild(
    string ModuleKey, string Name, long LinkFieldId, string LinkLabel, bool IsDetail, List<CatalogField> Fields, List<CatalogVia> Via);

public record ModuleCatalog(List<CatalogField> Fields, List<CatalogVia> Via, List<CatalogChild> Children);

/// <summary>
/// 設計器的欄位目錄：只列觀看者看得到、存檔也不會被 <see cref="Data.ReportRules"/> 拒絕的欄位
/// （非彙總類、帶出欄位不再是參照、參照目標可讀）。
/// </summary>
public class GetReportCatalogHandler(IModuleRepository modules, IDynamicAccessGuard guard)
    : IRequestHandler<GetReportCatalogRequest, ServiceResult>
{
    private sealed record Side(Module Module, EffectivePermissions Perms);

    public async Task<ServiceResult> Handle(GetReportCatalogRequest request, CancellationToken ct)
    {
        var main = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (main is not { Status: ModuleStatus.Active } || ModuleRelations.IsDetail(main))
            return ServiceResult.NotFound("模組不存在或不能當報表主模組");
        var access = await guard.AuthorizeAsync(main, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;

        var locale = request.Locale;
        var targets = new Dictionary<string, Side?>();
        async Task<Side?> TargetAsync(string key)
        {
            if (targets.TryGetValue(key, out var cached)) return cached;
            Side? side = null;
            if (await modules.GetByKeyAsync(key, includeChildren: true, ct) is { Status: ModuleStatus.Active } m
                && (await guard.AuthorizeAsync(m, PermissionAction.Read, ct)).Permissions is { } p)
                side = new Side(m, p);
            return targets[key] = side;
        }

        async Task<(List<CatalogField>, List<CatalogVia>)> ListAsync(Side side)
        {
            var fields = new List<CatalogField>();
            var via = new List<CatalogVia>();
            var excluded = ReportFieldEligibility.ExcludedFieldIds(side.Module);
            foreach (var f in Visible(side, excluded))
            {
                if (f.Type != FieldType.Reference) { fields.Add(ToField(f, locale)); continue; }
                if (f.Config.RefTarget is not { } target) continue;
                if (target.Kind == ReferenceTarget.EntityKind)
                {
                    fields.Add(ToField(f, locale));
                    via.Add(new CatalogVia(f.Id, f.LabelI18n.Resolve(locale), target.Key, [EntityTitle(target.Key, locale)]));
                    continue;
                }
                // 目標模組不可讀（或未啟用）時存檔其實允許，但每個值都會是（無權限）、分組只有一組，放進條件還會整張擋下，選了沒有意義，所以直接參照與帶出都不列
                if (await TargetAsync(target.Key) is not { } t) continue;
                fields.Add(ToField(f, locale));
                // 帶出只走一層：目標模組的參照欄位不列
                var targetFields = Visible(t, ReportFieldEligibility.ExcludedFieldIds(t.Module))
                    .Where(x => x.Type != FieldType.Reference).Select(x => ToField(x, locale)).ToList();
                if (targetFields.Count > 0) via.Add(new CatalogVia(f.Id, f.LabelI18n.Resolve(locale), target.Key, targetFields));
            }
            return (fields, via);
        }

        var (mainFields, mainVia) = await ListAsync(new Side(main, perms));

        var detailKeys = (await modules.ListDetailModulesAsync(main.Key, ct)).Select(x => x.Child.Key).ToHashSet();
        var children = new List<CatalogChild>();
        foreach (var (source, link) in await modules.ListIncomingReferenceFieldsAsync(main.Key, ct))
        {
            var isDetail = detailKeys.Contains(source.Key);
            // 刻意略過：別人的明細透過一般參照指回主模組時不列（與 RelatedTabs 同一套，明細只經表頭呈現）
            if (source.Status != ModuleStatus.Active || (!isDetail && ModuleRelations.IsDetail(source))) continue;
            var child = await modules.GetByKeyAsync(source.Key, includeChildren: true, ct);
            if (child is null) continue;
            // 明細模組走表頭授權，與 ReportModuleLoader 同一套
            var childAccess = isDetail
                ? await guard.AuthorizeDetailAsync(main, child, PermissionAction.Read, ct)
                : await guard.AuthorizeAsync(child, PermissionAction.Read, ct);
            if (childAccess.Permissions is not { } childPerms || !childPerms.CanSee(link)) continue;
            var (fields, via) = await ListAsync(new Side(child, childPerms));
            children.Add(new CatalogChild(child.Key, child.NameI18n.Resolve(locale), link.Id, link.LabelI18n.Resolve(locale), isDetail, fields, via));
        }
        return ServiceResult.Success(new ModuleCatalog(mainFields, mainVia, children));
    }

    private static IEnumerable<Field> Visible(Side side, IReadOnlySet<long> excluded) =>
        side.Module.Blocks.OrderBy(b => b.Sequence).SelectMany(b => b.Fields.OrderBy(f => f.Sequence))
            .Where(f => f.Status == FieldStatus.Active && side.Perms.CanSee(f) && !excluded.Contains(f.Id));

    /// <summary>
    /// 既有實體目標只有標題可帶，FieldId 固定為 <see cref="ReportRunner.EntityTitleFieldId"/>。
    /// 顯示的是標題，但存的與比對的是鍵，所以標成參照：設計器用記錄挑選器，運算子與條件值都是鍵。
    /// </summary>
    private static CatalogField EntityTitle(string entityKey, string locale) =>
        new(ReportRunner.EntityTitleFieldId, ReportText.Get(locale, ReportText.Title), FieldType.Reference, true, false, false, false, null, ReferenceTarget.EntityKind, entityKey);

    private static CatalogField ToField(Field f, string locale) =>
        new(f.Id, f.LabelI18n.Resolve(locale), f.Type, ReportFieldEligibility.CanGroup(f), ReportFieldEligibility.CanSum(f),
            ReportFieldEligibility.CanMinMax(f),
            f.Type is FieldType.Date or FieldType.DateTime || (f.Type == FieldType.Formula && f.Config.ResultType == "date"),
            f.Type is FieldType.Enum or FieldType.MultiEnum
                ? f.Options.OrderBy(o => o.Sequence).Select(o => new CatalogOption($"opt_{o.Id}", o.LabelI18n.Resolve(locale), o.Active)).ToList()
                : null,
            f.Type == FieldType.Reference ? f.Config.RefTarget?.Kind : null,
            f.Type == FieldType.Reference ? f.Config.RefTarget?.Key : null);
}
