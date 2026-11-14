using System.Text.Json;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 報表定義（存進 Report.DefinitionJson，camelCase）。Source 是 main|child；Bucket 是 day|week|month|quarter|year；
/// Kind 是 sum|avg|min|max|countMain|countChild|ratio；Viz 是 table|indicator|line|bar|donut。
/// Filters 為 AND 清單，Op 沿用 FilterOperator 的 camelCase 名稱。
/// </summary>
public record ReportDefinition(
    int Version, string MainModuleKey, ReportChild? Child,
    List<ReportFieldRef> Columns, List<ReportGroup> Groups, List<ReportMeasure> Measures,
    List<ReportFilter> Filters, ReportFieldRef? PrimaryDateField, ReportDateRange? DateRange,
    List<ReportSort> Sort, string Viz)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ReportDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static ReportDefinition Parse(string json) =>
        JsonSerializer.Deserialize<ReportDefinition>(json, Options) ?? throw new JsonException("報表定義不可為 null。");
}

public record ReportChild(string ModuleKey, long LinkFieldId);

public record ReportFieldRef(string Source, long FieldId, long? ViaFieldId);

public record ReportGroup(ReportFieldRef Field, string? Bucket);

public record ReportMeasure(string Id, string Kind, ReportFieldRef? Field, ReportFilter? Condition);

public record ReportFilter(ReportFieldRef Field, string Op, JsonElement? Value);

/// <summary>Kind 是 today|thisWeek|lastWeek|thisMonth|lastMonth|thisYear|lastYear|lastDays|lastWeeks|lastMonths；N 只用於 last* 系列。</summary>
public record ReportDateRange(string Kind, int? N)
{
    /// <summary>N 的上限（格式限制）；報表與任務看板的日期區間共用這一處。</summary>
    public const int MaxN = 3650;
}

public record ReportSort(string Key, bool Desc);
