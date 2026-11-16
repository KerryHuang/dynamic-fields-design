namespace DynamicModules.Domain.Reports;

/// <summary>
/// 一個欄位在一列中的值。State 是 ok|missing|restricted|empty：missing 是參照目標已刪除，restricted 是目標不在讀者範圍。
/// 值依欄位型別放在 Number、Text、Date 或 Multi（多選的選項鍵）。
/// </summary>
public record ReportCell(string State, decimal? Number, string? Text, DateOnly? Date, IReadOnlyList<string>? Multi);

/// <summary>報表的一列。ChildId 為 null 表示沒有合併子記錄；Cells 的 key 是 <see cref="ReportCalculator.KeyOf"/>。</summary>
public record ReportRow(long MainId, long? ChildId, IReadOnlyDictionary<string, ReportCell> Cells);

/// <summary>
/// 一個分組的結果。Values 的 key 是 ReportMeasure.Id；日期型的 min/max 以 DateOnly.DayNumber 回傳，由 runner 轉回日期。
/// </summary>
public record ReportGroupResult(
    IReadOnlyList<string> GroupKeys, IReadOnlyList<string> GroupLabels, IReadOnlyDictionary<string, decimal?> Values);

public record ReportCalcResult(
    IReadOnlyList<ReportGroupResult> Groups, IReadOnlyDictionary<string, decimal?> Total, bool DedupedNote, int RowCount);
