namespace DynamicModules.Domain.Reports;

/// <summary>SQL 路徑讀索引的哪一欄：Text＝TextValue、Ref＝RefKey、Boolean＝NumberValue（1／0）、Number＝ScaledValue（精確整數）、Date＝DateValue。</summary>
public enum ReportSqlKind { Text, Ref, Boolean, Number, Date }

/// <summary>
/// 一個欄位在 SQL 計畫裡的樣子。Scale 只對 Number 有意義；Multi 為真時分組不限 Seq = 0（一筆記錄落在多個分組）；
/// Bucket 只對 Date 分組有意義（day|week|month|quarter|year，null 為當天日期）。
/// </summary>
public sealed record ReportSqlField(long FieldId, ReportSqlKind Kind, int Scale, bool Multi, string? Bucket);

/// <summary>
/// 條件運算子，已換成索引值的語意：Eq、In 比相等（Negated 為否定，包含沒有值的記錄）；Gt、Gte、Lt、Lte、Between 用 Number 的 ScaledValue；
/// Range 用 Date 的半開區間 [Args[0], Args[1])（任一端為 null 表示不設限；Negated 為區間外加沒有值）；
/// Empty、NotEmpty 看有沒有索引列；Always、Never 是在 C# 就判定的恆真恆假（例如條件常數的小數位超過欄位 Scale）。
/// </summary>
public enum ReportSqlOp { Eq, In, Gt, Gte, Lt, Lte, Between, Range, Empty, NotEmpty, Always, Never }

/// <summary>Args 的型別依欄位種類：Text／Ref 是 string、Boolean／Number 是 long、Date 是 DateTime?。</summary>
public sealed record ReportSqlCondition(ReportSqlField Field, ReportSqlOp Op, IReadOnlyList<object?> Args, bool Negated = false);

/// <summary>Kind 同 <see cref="ReportMeasure.Kind"/>：sum|avg|min|max|countMain|countChild|ratio。</summary>
public sealed record ReportSqlMeasure(string Id, string Kind, ReportSqlField? Field, ReportSqlCondition? Condition);

/// <summary>一個模組的範圍：OwnerUserIds 為 null 表示全部，空集合表示沒有任何記錄（與 ListForReportAsync 一致）。</summary>
public sealed record ReportSqlScope(long ModuleId, IReadOnlyCollection<string>? OwnerUserIds);

/// <summary>資料狀態探測：Number 找 ScaledValue 不可用的列，Text 找被截斷的列（設計 §8.3）。</summary>
public enum ReportSqlCheckKind { Number, Text }

public sealed record ReportSqlCheck(long FieldId, ReportSqlCheckKind Kind);

/// <summary>
/// 規劃時讀到的欄位設定指紋；SQL 在讀取快照內重讀並比對，不同就放棄 SQL 路徑（設計 §8.4）。
/// Stamp 由 <see cref="ReportFieldStamp.Of"/> 產生。
/// </summary>
public sealed record ReportSqlConfigStamp(long FieldId, string Stamp);

/// <summary>聚合計畫：Filters 全部 AND（含主要日期區間）。</summary>
public sealed record ReportSqlQuery(
    ReportSqlScope Main, IReadOnlyList<ReportSqlField> Groups, IReadOnlyList<ReportSqlMeasure> Measures,
    IReadOnlyList<ReportSqlCondition> Filters, IReadOnlyList<ReportSqlCheck> Checks, IReadOnlyList<ReportSqlConfigStamp> Stamps);

/// <summary>縮小後備載入記錄用的條件（設計 §8.1）；Checks、Stamps 同 <see cref="ReportSqlQuery"/>，探測沒過就整個不縮小。</summary>
public sealed record ReportSqlNarrowing(
    IReadOnlyList<ReportSqlCondition> Filters, IReadOnlyList<ReportSqlCheck> Checks, IReadOnlyList<ReportSqlConfigStamp> Stamps);

/// <summary>
/// 一個量值的原始聚合：Count 是有值的筆數（sum、avg）；SumHi、SumLo 是 ScaledValue 除以與餘 10^9 後各自的加總；
/// MinScaled、MaxScaled 是數字的極值，MinDate、MaxDate 是日期的極值；Hit 是 ratio 符合條件的筆數。
/// </summary>
public sealed record ReportSqlMeasureRaw(
    long Count, long SumHi, long SumLo, long? MinScaled, long? MaxScaled, DateTime? MinDate, DateTime? MaxDate, long Hit);

/// <summary>Keys 的順序同 <see cref="ReportSqlQuery.Groups"/>：Text／Ref／Date 是 string，Number 是 long?（ScaledValue），Boolean 是 string（true、false 或空）。</summary>
public sealed record ReportSqlRow(IReadOnlyList<object?> Keys, long RowCount, IReadOnlyList<ReportSqlMeasureRaw> Measures);

/// <summary>FallbackReason 不為 null 時代表 SQL 路徑放棄，Groups、Total 無意義。</summary>
public sealed record ReportSqlOutcome(string? FallbackReason, IReadOnlyList<ReportSqlRow> Groups, ReportSqlRow? Total)
{
    public static ReportSqlOutcome Fallback(string reason) => new(reason, [], null);
}

/// <summary>欄位設定指紋：只含會改變 SQL 解讀的設定。</summary>
public static class ReportFieldStamp
{
    public static string Of(DynamicModules.Domain.Entities.Dynamic.Field f) =>
        $"{f.Type}|{f.Status}|{f.Config.Scale}|{f.Config.ResultType}|{f.Config.IsFilterable}|{f.Config.IsSortable}|{f.Config.MaxLength}";
}
