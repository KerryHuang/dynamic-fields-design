using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>Locale 為 null 時用 zh-TW。</summary>
public record ReportRunOptions(ReportDateRange? RangeOverride, IReadOnlyList<string>? DrillGroupKeys, string? Locale = null);

/// <summary>
/// 報表執行結果。State 是 ok|empty|restricted|fieldMissing|moduleDisabled|tooMany，HTTP 一律 200；ok、empty 以外不帶任何數字。
/// DetailRows 每列的順序對齊 Columns 中 Role 為 field 的欄。
/// </summary>
public record ReportResult(
    string State, string? Message, string RowMeaning, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportGroupResult>? Groups, IReadOnlyDictionary<string, decimal?>? Total,
    IReadOnlyList<IReadOnlyList<object?>>? DetailRows, bool DedupedNote, DateOnly BasisDate, ReportAppliedRange? AppliedRange);

/// <summary>
/// Key：group、field 是 ReportCalculator.KeyOf，measure 是量值 Id；ReportSort.Key 用同一套鍵。Role 是 group|measure|field。
/// ValueType 是 text|number|percent|date|dateTime|ratio|dayNumber；dayNumber 是日期型 min/max，值為 DateOnly.DayNumber。
/// </summary>
public record ReportColumn(string Key, string Label, string Role, string ValueType);

/// <summary>實際套用的日期區間（含頭含尾）與它的來源設定。</summary>
public record ReportAppliedRange(DateOnly From, DateOnly To, string Kind, int? N);
