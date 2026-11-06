using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>
/// Locale 為 null 時用 zh-TW。後面幾項是儀表板卡片選項（只有 <see cref="ReportRunner.RunCardAsync"/> 看）：
/// TopN 只留排序後前 N 組（總計不變、不併「其他」）；SortKey／SortDesc 取代報表的排序；Cumulative 加累計占比；
/// ComparePeriods 加與前 N 期平均的比較。Window 直接指定日期範圍（含頭含尾），取代 RangeOverride 與報表的區間，期間比較內部用。
/// </summary>
public record ReportRunOptions(
    ReportDateRange? RangeOverride, IReadOnlyList<string>? DrillGroupKeys, string? Locale = null,
    int? TopN = null, string? SortKey = null, bool SortDesc = false, bool Cumulative = false, int? ComparePeriods = null,
    (DateOnly From, DateOnly ToInclusive)? Window = null);

/// <summary>
/// 報表執行結果。State 是 ok|empty|restricted|fieldMissing|moduleDisabled|tooMany，HTTP 一律 200；ok、empty 以外不帶任何數字。
/// BasisDate 與後端的今天不同時 BasisIsFrozen 為 true（Demo 資料日期停在產生那天），前端據此標示資料基準日。
/// Cumulative 對齊 Groups，是第一個量值的累計占比（百分比）。IgnoresDateFilter 表示畫面送了日期篩選但這張卡片沒有採用（沒有主要日期欄位，或卡片有自己的區間）。
/// DetailRows 每列的順序對齊 Columns 中 Role 為 field 的欄。DetailTotal 是明細全部的列數；DetailTruncated 表示畫面上限截掉了部分列（<see cref="ReportRules.CapDetail"/>），Total 不受影響。
/// </summary>
public record ReportResult(
    string State, string? Message, string RowMeaning, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportGroupResult>? Groups, IReadOnlyDictionary<string, decimal?>? Total,
    IReadOnlyList<IReadOnlyList<object?>>? DetailRows, bool DedupedNote, DateOnly BasisDate, ReportAppliedRange? AppliedRange, bool BasisIsFrozen = false,
    int? DetailTotal = null, bool DetailTruncated = false,
    IReadOnlyList<decimal?>? Cumulative = null, ReportCompare? Compare = null, bool IgnoresDateFilter = false);

/// <summary>與前幾期平均的比較：PreviousAverage 是前 N 期第一個量值的平均，DeltaPercent 是目前值相對它的百分比（平均為 0 時為 null）。</summary>
public record ReportCompare(decimal? PreviousAverage, decimal? DeltaPercent);

/// <summary>
/// Key：group、field 是 ReportCalculator.KeyOf，measure 是量值 Id；ReportSort.Key 用同一套鍵。Role 是 group|measure|field。
/// ValueType 是 text|number|percent|date|dateTime|ratio|dayNumber；dayNumber 是日期型 min/max，值為 DateOnly.DayNumber。
/// </summary>
public record ReportColumn(string Key, string Label, string Role, string ValueType);

/// <summary>實際套用的日期區間（含頭含尾）與它的來源設定。</summary>
public record ReportAppliedRange(DateOnly From, DateOnly To, string Kind, int? N);

/// <summary>報表清單一筆；SharedGroupIds 對非擁有者也照實回傳。</summary>
public record ReportItem(long Id, string Name, string OwnerName, bool IsOwner, List<long> SharedGroupIds, string MainModuleKey);

/// <summary>分享對話框的群組選項。</summary>
public record ReportShareTarget(long Id, I18nText Name);

/// <summary>單張報表；Definition 是儲存的定義原樣（被分享者唯讀）。CanExport 與匯出同一套判斷：涉及的模組都有 PrintExport。</summary>
public record ReportDetail(
    long Id, string Name, string OwnerName, bool IsOwner, List<long> SharedGroupIds, string MainModuleKey, JsonElement Definition,
    bool CanExport);

/// <summary>下鑽結果：同 <see cref="ReportResult"/>，明細最多 <see cref="ReportRules.DrillLimit"/> 列，超過時 Truncated 為 true。</summary>
public sealed record DrillResult : ReportResult
{
    public DrillResult(ReportResult result, bool truncated) : base(result) => Truncated = truncated;
    public bool Truncated { get; }
}

public static class ReportMapper
{
    public static ReportItem ToItem(this Report r, ReportViewer viewer, IReadOnlyDictionary<string, string> names) =>
        new(r.Id, r.Name, names.GetValueOrDefault(r.OwnerUserId, r.OwnerUserId), r.OwnerUserId == viewer.UserId,
            r.Shares.Select(s => s.GroupId).Order().ToList(), MainKeyOf(r));

    /// <param name="definition">依觀看者遮蔽後的定義（<see cref="ReportMasking.Mask"/>）。</param>
    public static ReportDetail ToDetail(this Report r, ReportViewer viewer, IReadOnlyDictionary<string, string> names, JsonElement definition, bool canExport)
    {
        var item = r.ToItem(viewer, names);
        return new(item.Id, item.Name, item.OwnerName, item.IsOwner, item.SharedGroupIds, item.MainModuleKey, definition, canExport);
    }

    private static string MainKeyOf(Report r)
    {
        try { return ReportDefinition.Parse(r.DefinitionJson).MainModuleKey ?? ""; }
        catch (JsonException) { return ""; }
    }
}
