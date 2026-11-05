using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.ExportReport;

/// <summary>
/// 匯出 xlsx：要有所有涉及模組的 PrintExport，否則報表會變成繞過匯出權限的路（spec §7）。數字取自 runner，與畫面一致。
/// 有明細欄位就匯出明細，否則匯出分組表（最後一列合計）。
/// </summary>
public class ExportReportHandler(ReportAccess access, ReportModuleLoader loader, ReportRunner runner, IBusinessTime time)
    : IRequestHandler<ExportReportRequest, ServiceResult>
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string TotalLabel = "合計";
    // spec §6.3：有合併且彙總了主模組欄位時必須說明，使用者接受去重的代價
    private const string DedupedText = "主模組欄位的數字每筆主記錄只算一次，明細列相加不等於合計。";

    public async Task<ServiceResult> Handle(ExportReportRequest request, CancellationToken ct)
    {
        var (report, _) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);

        var definition = ReportDefinition.Parse(report.DefinitionJson);
        var (set, denied) = await loader.LoadAsync(definition, PermissionAction.PrintExport, ct);
        if (set is null) return denied!;

        var result = await runner.RunAsync(definition, new ReportRunOptions(null, null, request.Locale), ct);
        if (result.State is not ("ok" or "empty"))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["report"] = [result.Message ?? result.State] });

        var (header, rows) = result.DetailRows is not null ? Detail(result) : Grouped(result);
        // 明細表沒有位置放量值，合計接在明細下方，每個量值一列
        if (result.DetailRows is not null && result.Total is { Count: > 0 } total)
        {
            rows.Add([]);
            foreach (var m in result.Columns.Where(c => c.Role == "measure"))
                rows.Add([m.Label, Measure(m, total.GetValueOrDefault(m.Key))]);
        }
        if (result.DedupedNote)
        {
            rows.Add([]);
            rows.Add([DedupedText]);
        }
        var content = XlsxWriter.Write(report.Name, header, rows);
        return ServiceResult.Success(new ExportFile($"report-{report.Id}-{time.Now:yyyyMMdd-HHmm}.xlsx", XlsxType, content));
    }

    private static (List<string>, List<IReadOnlyList<object>>) Detail(ReportResult r)
    {
        var columns = r.Columns.Where(c => c.Role == "field").ToList();
        return (columns.Select(c => c.Label).ToList(),
            r.DetailRows!.Select(row => (IReadOnlyList<object>)row.Select(Cell).ToList()).ToList());
    }

    private static (List<string>, List<IReadOnlyList<object>>) Grouped(ReportResult r)
    {
        var columns = r.Columns.Where(c => c.Role is "group" or "measure").ToList();
        var measures = columns.Where(c => c.Role == "measure").ToList();
        object Value(ReportColumn c, IReadOnlyDictionary<string, decimal?> values) => Measure(c, values.GetValueOrDefault(c.Key));

        var rows = new List<IReadOnlyList<object>>();
        foreach (var g in r.Groups ?? [])
            rows.Add(g.GroupLabels.Select(l => (object)l).Concat(measures.Select(m => Value(m, g.Values))).ToList());
        var groupCount = columns.Count - measures.Count;
        var total = r.Total ?? new Dictionary<string, decimal?>();
        rows.Add(Enumerable.Range(0, groupCount).Select(i => (object)(i == 0 ? TotalLabel : ""))
            .Concat(measures.Select(m => Value(m, total))).ToList());
        return (columns.Select(c => c.Label).ToList(), rows);
    }

    private static object Measure(ReportColumn c, decimal? v) =>
        v is { } n
            ? c.ValueType == "dayNumber" ? DateOnly.FromDayNumber((int)n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : n
            : "";

    private static object Cell(object? v) => v switch
    {
        null => "",
        decimal d => d,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
