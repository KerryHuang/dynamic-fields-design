using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.ReportService;

public partial class ReportRunner
{
    private static readonly string[] AdditiveKinds = ["sum", "countMain", "countChild"];

    /// <summary>
    /// 儀表板卡片的執行：<see cref="RunAsync"/> 加上卡片選項（spec §4）。前 N 名與累計占比靠排序後的分組，期間比較每期各跑一次，
    /// 所以每期都走相同的去重與權限。非 ok 的結果（無權限、欄位失效…）原樣回傳，不帶選項。
    /// </summary>
    public async Task<ReportResult> RunCardAsync(ReportDefinition d, ReportRunOptions o, CancellationToken ct)
    {
        var first = d.Measures.FirstOrDefault();
        var options = o;
        // 累計占比要依數值由大到小；只設前 N 名又沒有任何排序時，也以第一個量值由大到小，否則「前 N 名」沒有意義
        if (first is not null && (o.Cumulative || (o.TopN is not null && o.SortKey is null && d.Sort.Count == 0)))
            options = o with { SortKey = first.Id, SortDesc = true };

        var result = await RunAsync(d, options, ct);
        if (result.State is not ("ok" or "empty")) return result;

        if (result.Groups is { } groups && first is not null)
        {
            if (o.Cumulative && AdditiveKinds.Contains(first.Kind))
            {
                var all = groups.Sum(g => g.Values.GetValueOrDefault(first.Id) ?? 0);
                var shown = o.TopN is { } n ? groups.Take(n) : groups;
                result = result with { Cumulative = ReportCardMath.CumulativePercent([.. shown.Select(g => g.Values.GetValueOrDefault(first.Id))], all) };
            }
            if (o.TopN is { } top) result = result with { Groups = [.. groups.Take(top)] };
        }

        if (o.ComparePeriods is { } periods && first is not null && d.PrimaryDateField is not null
            && (o.RangeOverride ?? d.DateRange) is { } range && result.Total is { } total)
            result = result with { Compare = await CompareAsync(d, o, first, range, periods, result.BasisDate, total, ct) };
        return result;
    }

    private async Task<ReportCompare?> CompareAsync(
        ReportDefinition d, ReportRunOptions o, ReportMeasure measure, ReportDateRange range, int periods, DateOnly basis,
        IReadOnlyDictionary<string, decimal?> total, CancellationToken ct)
    {
        var additive = AdditiveKinds.Contains(measure.Kind);
        decimal? Value(IReadOnlyDictionary<string, decimal?>? t) => t?.GetValueOrDefault(measure.Id) ?? (additive ? 0 : null);
        if (Value(total) is not { } current) return null;

        var previous = new List<decimal>();
        foreach (var window in ReportCardMath.PreviousWindows(range, basis, periods))
        {
            var run = await RunAsync(d, new ReportRunOptions(null, null, o.Locale, Window: window), ct);
            if (run.State is not ("ok" or "empty")) return null;
            if (Value(run.Total) is { } value) previous.Add(value);
        }
        var (average, delta) = ReportCardMath.Compare(current, previous);
        return new ReportCompare(average, delta);
    }
}
