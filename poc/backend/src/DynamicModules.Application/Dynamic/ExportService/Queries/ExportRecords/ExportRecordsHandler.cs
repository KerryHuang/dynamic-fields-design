using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;

/// <summary>匯出 CSV／xlsx（決策 5）：權限 PrintExport，篩選／排序／資料範圍同列表，不分頁。</summary>
public class ExportRecordsHandler(
    IModuleRepository modules, IRecordRepository records, IPermissionRepository permissions, FieldTypeRegistry registry,
    IBusinessTime time, IDynamicAccessGuard guard, ReferenceResolver resolver, ExportOptions options)
    : IRequestHandler<ExportRecordsRequest, ServiceResult>
{
    private const string CsvType = "text/csv; charset=utf-8";
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<ServiceResult> Handle(ExportRecordsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.PrintExport, ct);
        if (access.Permissions is not { } perms) return access.Denied!;

        var errors = new Dictionary<string, string[]>();
        var isCsv = request.Format == "csv";
        if (!isCsv && request.Format != "xlsx") errors["format"] = ["格式只能是 csv 或 xlsx"];
        var query = await RecordQueryBuilder.BuildAsync(modules, registry, module, perms, request.Filter, request.SortBy,
            request.SortOrder, request.Q, time.Today, 1, 1, errors, ct);
        if (query is null) return ServiceResult.Invalid(errors);

        // 先只查總數，超過上限就不載入資料列
        var (_, total) = await records.QueryAsync(query, ct);
        if (total > options.MaxRecords)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["filter"] = [$"匯出最多 {options.MaxRecords} 筆，請加篩選條件"] });
        var (items, _) = await records.QueryAsync(query with { Size = Math.Max(total, 1) }, ct);

        var locale = string.IsNullOrEmpty(request.Locale) ? "zh-TW" : request.Locale;
        var fields = module.Blocks.OrderBy(b => b.Sequence).ThenBy(b => b.Id)
            .SelectMany(b => b.Fields.OrderBy(f => f.Sequence).ThenBy(f => f.Id))
            .Where(f => f.Status == FieldStatus.Active && perms.CanSee(f)).ToList();
        var userNames = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var refs = await resolver.ResolveAsync(module, perms, items, ct);

        var header = fields.Select(f => f.LabelI18n.Resolve(locale)).ToList();
        var parsed = items.Select(r => (Record: r, Values: RecordValueProcessor.Parse(r.ValuesJson))).ToList();
        var stamp = time.Now.ToString("yyyyMMdd-HHmm");

        if (isCsv)
        {
            var rows = parsed.Select(p => (IReadOnlyList<string>)fields.Select(f =>
            {
                var text = Text(f, p.Values, locale, refs.GetValueOrDefault(p.Record.Id), userNames);
                // 公式注入防護套用在數字以外的所有欄位：參照標題、選項標籤等也可能來自使用者輸入
                return FieldDisplay.IsNumericCell(f) || f.Type == FieldType.Percent ? text : CsvWriter.EscapeFormula(text);
            }).ToList());
            return ServiceResult.Success(new ExportFile($"{module.Key}-{stamp}.csv", CsvType, CsvWriter.Write(header, rows)));
        }

        var xRows = parsed.Select(p => (IReadOnlyList<object>)fields.Select(f =>
        {
            var text = Text(f, p.Values, locale, refs.GetValueOrDefault(p.Record.Id), userNames);
            return FieldDisplay.IsNumericCell(f) && decimal.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? (object)d : text;
        }).ToList());
        return ServiceResult.Success(new ExportFile(
            $"{module.Key}-{stamp}.xlsx", XlsxType, XlsxWriter.Write(module.NameI18n.Resolve(locale), header, xRows)));
    }

    private static string Text(Field f, IReadOnlyDictionary<string, System.Text.Json.JsonElement?> values, string locale,
        IReadOnlyDictionary<string, ReferenceInfo>? refs, IReadOnlyDictionary<string, string> userNames) =>
        FieldDisplay.Format(f, values.GetValueOrDefault(f.Key), locale, refs?.GetValueOrDefault(f.Key), userNames);
}
