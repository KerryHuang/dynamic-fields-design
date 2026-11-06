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
/// 有明細欄位就匯出明細，否則匯出分組表（最後一列總計）。
/// </summary>
public class ExportReportHandler(ReportAccess access, ReportModuleLoader loader, ReportRunner runner, IBusinessTime time)
    : IRequestHandler<ExportReportRequest, ServiceResult>
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

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

        var (header, rows) = result.DetailRows is not null ? Detail(result) : Grouped(result, ReportText.Get(request.Locale, ReportText.ExportTotal));
        // 明細表沒有位置放量值，總計接在明細下方，每個量值一列
        if (result.DetailRows is not null && result.Total is { Count: > 0 } total)
        {
            rows.Add([]);
            foreach (var m in result.Columns.Where(c => c.Role == "measure"))
                rows.Add([m.Label, Measure(m, total.GetValueOrDefault(m.Key))]);
        }
        if (result.DedupedNote)
        {
            // spec §6.3：列相加對不上總計時必須說明，使用者接受去重的代價
            rows.Add([]);
            rows.Add([ReportText.Get(request.Locale, ReportText.DedupedNote)]);
        }
        var content = XlsxWriter.Write(report.Name, header, rows);
        return ServiceResult.Success(new ExportFile($"report-{report.Id}-{time.Now:yyyyMMdd-HHmm}.xlsx", XlsxType, content));
    }

    private static (List<string>, List<IReadOnlyList<object>>) Detail(ReportResult r)
    {
        var columns = r.Columns.Where(c => c.Role == "field").ToList();
        return (columns.Select(c => c.Label).ToList(),
            r.DetailRows!.Select(row => (IReadOnlyList<object>)row.Select((v, i) => Cell(columns[i], v)).ToList()).ToList());
    }

    private static (List<string>, List<IReadOnlyList<object>>) Grouped(ReportResult r, string totalLabel)
    {
        var columns = r.Columns.Where(c => c.Role is "group" or "measure").ToList();
        var measures = columns.Where(c => c.Role == "measure").ToList();
        object Value(ReportColumn c, IReadOnlyDictionary<string, decimal?> values) => Measure(c, values.GetValueOrDefault(c.Key));

        var rows = new List<IReadOnlyList<object>>();
        foreach (var g in r.Groups ?? [])
            rows.Add(g.GroupLabels.Select(l => (object)l).Concat(measures.Select(m => Value(m, g.Values))).ToList());
        var groupCount = columns.Count - measures.Count;
        var total = r.Total ?? new Dictionary<string, decimal?>();
        rows.Add(Enumerable.Range(0, groupCount).Select(i => (object)(i == 0 ? totalLabel : ""))
            .Concat(measures.Select(m => Value(m, total))).ToList());
        return (columns.Select(c => c.Label).ToList(), rows);
    }

    private static object Measure(ReportColumn c, decimal? v) => v switch
    {
        null => "",
        { } n when c.ValueType == "dayNumber" => DateOnly.FromDayNumber((int)n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        { } n => Number(c, n),
    };

    /// <summary>ratio 是 0 到 1；percent 欄位存 12.5 代表 12.5%，Excel 的百分比格式要 0.125。</summary>
    private static object Number(ReportColumn c, decimal n) => c.ValueType switch
    {
        "ratio" => new XlsxPercent(n),
        "percent" => new XlsxPercent(n / 100),
        _ => n,
    };

    private static object Cell(ReportColumn c, object? v) => v switch
    {
        null => "",
        decimal d => Number(c, d),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
