using System.Net;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.PermissionService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>
/// 列印：先驗權限（模組可讀、PrintExport、資料範圍），再由後端算值——只放使用者有效權限看得到的啟用欄位，
/// 其餘（停用、被隱藏、已刪除、範本寫錯的）一律輸出空字串。範本在存檔與列印時各淨化一次，值全部跳脫。
/// Closed 模組可以列印（資料唯讀不影響讀取）。
/// </summary>
public class PrintRecordHandler(
    IModuleRepository modules, IRecordRepository records, IPrintTemplateRepository templates, IDynamicAccessGuard guard,
    ReferenceResolver resolver, IPermissionRepository permissions, IBusinessTime time)
    : IRequestHandler<PrintRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PrintRecordRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.RecordId, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        // 資料範圍與欄位權限以 Read 為準；PrintExport 另外要有，兩者缺一不可（被撤銷 Read 就不能靠列印取得資料）
        var access = await guard.AuthorizeAsync(module!, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        var printAccess = await guard.AuthorizeAsync(module!, PermissionAction.PrintExport, ct);
        if (printAccess.Permissions is null) return printAccess.Denied!;
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var template = await templates.GetAsync(module!.Id, request.TemplateKey, ct);
        if (template is not { Active: true }) return ServiceResult.NotFound(PrintTemplateRules.NotFound);

        var locale = string.IsNullOrEmpty(request.Locale) ? "zh-TW" : request.Locale;
        var body = PrintTemplateSanitizer.Sanitize(template.Body);
        // 舊資料或繞過儲存驗證寫入的範本：迴圈標記跨標籤會在展開後拼出未淨化的標籤，直接拒絕
        if (PrintTemplateEngine.HasLoopMarkerInsideTag(body))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["body"] = [PrintTemplateEngine.LoopMarkerInTagMessage] });
        var placeholders = PrintTemplateEngine.Placeholders(body);
        var userNames = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName);

        var refs = await resolver.ResolveAsync(module, perms, [record], ct);
        var headerValues = RecordValueProcessor.Parse(record.ValuesJson);
        var visible = VisibleFields(module, perms);
        var titleVisible = module.TitleFieldId is { } tid && visible.Values.Any(f => f.Id == tid);

        string? Header(string key) => key switch
        {
            "_title" => titleVisible ? record.Title : "",
            "_printedAt" => time.Now.ToString("yyyy-MM-dd HH:mm"),
            _ => ValueOf(visible, key, headerValues, refs.GetValueOrDefault(record.Id), locale, userNames),
        };

        var loops = new Dictionary<string, IReadOnlyList<Func<string, string?>>>();
        if (placeholders.Loops.Count > 0)
        {
            var children = await DetailWriter.ReadableDetailModulesAsync(modules, module, ct);
            foreach (var moduleKey in placeholders.Loops.Keys)
            {
                var child = children.FirstOrDefault(c => c.Key == moduleKey);
                if (child is null) continue;
                var childAccess = await guard.AuthorizeDetailAsync(module, child, PermissionAction.Read, ct);
                if (childAccess.Permissions is not { } childPerms) continue;
                var parentField = ModuleRelations.DetailFieldOf(child)!;
                var rows = await records.ListByRefAsync(parentField.Id, record.Id.ToString(), ct);
                var rowRefs = await resolver.ResolveAsync(child, childPerms, rows, ct);
                var childVisible = VisibleFields(child, childPerms);
                loops[moduleKey] = rows.Select(r =>
                {
                    var values = RecordValueProcessor.Parse(r.ValuesJson);
                    var shown = rowRefs.GetValueOrDefault(r.Id);
                    return (Func<string, string?>)(key => ValueOf(childVisible, key, values, shown, locale, userNames));
                }).ToList();
            }
        }

        // 以最終輸出為準：展開後再淨化一次（縱深防禦，即使展開拼出了奇怪的結構也只會輸出白名單內的東西）
        var html = PrintTemplateSanitizer.Sanitize(PrintTemplateEngine.Render(body, Header, key => loops.GetValueOrDefault(key) ?? []));
        var title = WebUtility.HtmlEncode(template.NameI18n.Resolve(locale));
        return ServiceResult.Success(new PrintedDocument(PrintDocument.Wrap(title, html, locale)));
    }

    private static Dictionary<string, Field> VisibleFields(Module module, EffectivePermissions perms) =>
        module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active && perms.CanSee(f)).ToDictionary(f => f.Key);

    private static string? ValueOf(
        Dictionary<string, Field> visible, string key, IReadOnlyDictionary<string, System.Text.Json.JsonElement?> values,
        IReadOnlyDictionary<string, ReferenceInfo>? refs, string locale, IReadOnlyDictionary<string, string> userNames) =>
        visible.TryGetValue(key, out var field)
            ? PrintValueFormat.Format(field, values.GetValueOrDefault(key), locale, refs?.GetValueOrDefault(key), userNames)
            : null;
}

/// <summary>列印用的外殼文件：只有內嵌樣式，沒有腳本與外部資源；用 srcdoc 顯示時不承接 API 的 CSP，所以文件自帶一份。</summary>
internal static class PrintDocument
{
    public static string Wrap(string encodedTitle, string body, string locale) => $$"""
        <!doctype html>
        <html lang="{{WebUtility.HtmlEncode(locale)}}">
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
        <title>{{encodedTitle}}</title>
        <style>
        @page { size: A4; margin: 12mm; }
        body { font-family: "Noto Sans TC", "PingFang TC", "Microsoft JhengHei", sans-serif; font-size: 12pt; line-height: 1.5; color: #000; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #444; padding: 4px 6px; text-align: left; vertical-align: top; }
        th { background: #eee; }
        h1, h2, h3 { margin: 0 0 8px; }
        </style>
        </head>
        <body>
        {{body}}
        </body>
        </html>
        """;
}
