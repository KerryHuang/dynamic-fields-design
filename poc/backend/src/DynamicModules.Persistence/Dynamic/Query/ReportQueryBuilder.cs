using System.Text;
using Dapper;
using DynamicModules.Domain.Reports;
using DynamicModules.Persistence.Dynamic.Dialects;

namespace DynamicModules.Persistence.Dynamic.Query;

/// <summary>
/// 把報表的 SQL 計畫轉成參數化 SQL（設計 §5）。SQL 只由常數與白名單片段組成；欄位 id、模組 id、值、範圍名單一律走參數。
/// 每個 Dynamic_Record 別名都必須帶 ModuleId、DeleteFlag = 0 與資料範圍（fail-closed）：範圍由 <see cref="ReportSqlScope"/> 強制傳入，
/// OwnerUserIds 為 null 是「全部」的明確值。
/// </summary>
public static class ReportQueryBuilder
{
    public sealed record Built(string Sql, DynamicParameters Parameters);

    private sealed class Ctx
    {
        public DynamicParameters Parameters { get; } = new();
        private int _n;

        public string Add(object? value)
        {
            var name = $"p{_n++}";
            Parameters.Add(name, value);
            return "@" + name;
        }
    }

    private const long Split = 1_000_000_000;

    /// <summary>Dynamic_RecordIndex 主鍵 (RecordId, FieldId, Seq) 的索引名（SQLite 為主鍵自動命名）。</summary>
    private const string PrimaryKeyIndex = "sqlite_autoindex_Dynamic_RecordIndex_1";

    private static string Column(ReportSqlKind kind) => kind switch
    {
        ReportSqlKind.Text => "TextValue",
        ReportSqlKind.Ref => "RefKey",
        ReportSqlKind.Boolean => "NumberValue",
        ReportSqlKind.Number => "ScaledValue",
        ReportSqlKind.Date => "DateValue",
        _ => throw new InvalidOperationException($"未知的索引欄種類 {kind}"),
    };

    /// <summary>範圍條件：ModuleId、DeleteFlag 與資料範圍。所有 Dynamic_Record 別名都走這個方法，缺範圍不可能通過編譯。</summary>
    private static void AppendScope(StringBuilder where, string alias, ReportSqlScope scope, Ctx ctx)
    {
        where.Append($"{alias}.ModuleId = {ctx.Add(scope.ModuleId)} AND {alias}.DeleteFlag = 0");
        if (scope.OwnerUserIds is null) return;
        if (scope.OwnerUserIds.Count == 0)
        {
            where.Append(" AND 1 = 0");
            return;
        }
        where.Append($" AND {alias}.OwnerUserId IN (").Append(string.Join(", ", scope.OwnerUserIds.Select(id => ctx.Add(id)))).Append(')');
    }

    /// <summary>索引列上的比較（不含 RecordId 與 FieldId）；空字串表示沒有比較（Empty、NotEmpty）。</summary>
    private static string Compare(ReportSqlCondition c, string alias, Ctx ctx)
    {
        var col = $"{alias}.{Column(c.Field.Kind)}";
        switch (c.Op)
        {
            // 文字欄位存空字串時索引有一列，但報表把空字串當沒有值（與 ValueCell 一致），所以有沒有值要看 TextValue 是否非空
            case ReportSqlOp.Empty or ReportSqlOp.NotEmpty when c.Field.Kind == ReportSqlKind.Text: return $"{col} <> ''";
            case ReportSqlOp.Eq: return $"{col} = {ctx.Add(c.Args[0])}";
            case ReportSqlOp.In: return $"{col} IN ({string.Join(", ", c.Args.Select(a => ctx.Add(a)))})";
            case ReportSqlOp.Gt: return $"{col} > {ctx.Add(c.Args[0])}";
            case ReportSqlOp.Gte: return $"{col} >= {ctx.Add(c.Args[0])}";
            case ReportSqlOp.Lt: return $"{col} < {ctx.Add(c.Args[0])}";
            case ReportSqlOp.Lte: return $"{col} <= {ctx.Add(c.Args[0])}";
            case ReportSqlOp.Between: return $"{col} >= {ctx.Add(c.Args[0])} AND {col} <= {ctx.Add(c.Args[1])}";
            case ReportSqlOp.Range:
                var parts = new List<string>();
                if (c.Args[0] is not null) parts.Add($"{col} >= {ctx.Add(c.Args[0])}");
                if (c.Args[1] is not null) parts.Add($"{col} < {ctx.Add(c.Args[1])}");
                return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
            default: return "";
        }
    }

    /// <summary>
    /// 一個條件對某筆記錄（recordIdExpr）的判斷，語意同 <c>ReportRunner.Matches</c>：有值才可能符合比較類；
    /// 否定類（ne、notIn、區間外）與 Empty 包含沒有值的記錄。
    /// </summary>
    private static string Predicate(ReportSqlCondition c, string recordIdExpr, Ctx ctx, ref int aliasCounter)
    {
        switch (c.Op)
        {
            case ReportSqlOp.Always: return "1 = 1";
            case ReportSqlOp.Never: return "1 = 0";
        }
        var alias = $"i{aliasCounter++}";
        var cmp = Compare(c, alias, ctx);
        // 逐筆記錄查：一定要走 (RecordId, FieldId, Seq) 的主鍵索引。規劃器會被欄位值索引（日期範圍等）吸引，變成每筆記錄各掃一遍整個範圍
        var exists = $"EXISTS (SELECT 1 FROM Dynamic_RecordIndex {alias} INDEXED BY {PrimaryKeyIndex} WHERE {alias}.RecordId = {recordIdExpr} AND {alias}.FieldId = {ctx.Add(c.Field.FieldId)}"
            + (cmp.Length == 0 ? "" : " AND " + cmp) + ")";
        var negate = c.Op == ReportSqlOp.Empty ? true : c.Op == ReportSqlOp.NotEmpty ? false : c.Negated;
        return negate ? "NOT " + exists : exists;
    }

    /// <summary>驅動條件：能由既有索引直接定位的正向條件（Text／Ref 的 Eq、In，Date 的 Range），否則 null。</summary>
    public static bool CanDrive(ReportSqlCondition c) =>
        !c.Negated && c.Field.Kind is ReportSqlKind.Text or ReportSqlKind.Ref && c.Op is ReportSqlOp.Eq or ReportSqlOp.In
        || !c.Negated && c.Field.Kind == ReportSqlKind.Date && c.Op == ReportSqlOp.Range;

    /// <summary>估計驅動條件命中筆數用：數到 cap 為止，成本最多 cap 個索引項。</summary>
    public static Built CappedCount(ReportSqlCondition c, int cap)
    {
        var ctx = new Ctx();
        var cmp = Compare(c, "i", ctx);
        return new Built($"SELECT COUNT(*) FROM (SELECT 1 FROM Dynamic_RecordIndex i WHERE i.FieldId = {ctx.Add(c.Field.FieldId)} AND {cmp} LIMIT {ctx.Add(cap)})", ctx.Parameters);
    }

    private static string DriverFrom(ReportSqlCondition driver, Ctx ctx)
    {
        var cmp = Compare(driver, "di", ctx);
        // 單值欄位每筆記錄最多一列（Seq = 0），不會重複；只有多選才需要 DISTINCT（它在 300 萬筆時要建暫存 B 樹，約佔三分之一時間）
        var distinct = driver.Field.Multi ? "DISTINCT " : "";
        return $"(SELECT {distinct}di.RecordId FROM Dynamic_RecordIndex di WHERE di.FieldId = {ctx.Add(driver.Field.FieldId)} AND {cmp}) d CROSS JOIN Dynamic_Record r ON r.Id = d.RecordId";
    }

    /// <summary>資料狀態探測：從部分索引出發只走壞列，再回頭檢查範圍；有任何一列就回 1 列。</summary>
    public static Built Probe(ReportSqlCheck check, ReportSqlScope scope)
    {
        var ctx = new Ctx();
        // 部分索引只收壞列，探測成本跟壞列數成正比；規劃器會改選整個欄位的 NumberValue 索引（掃全部列），所以明確指定
        var (bad, index) = check.Kind == ReportSqlCheckKind.Number
            ? ("t.ScaledValue IS NULL AND t.NumberValue IS NOT NULL", "IX_Dynamic_RecordIndex_NoScaled")
            : ("t.TextTruncated = 1", "IX_Dynamic_RecordIndex_Truncated");
        var where = new StringBuilder($"t.FieldId = {ctx.Add(check.FieldId)} AND {bad} AND ");
        AppendScope(where, "r", scope, ctx);
        return new Built($"SELECT 1 FROM Dynamic_RecordIndex t INDEXED BY {index} JOIN Dynamic_Record r ON r.Id = t.RecordId WHERE {where} LIMIT 1", ctx.Parameters);
    }

    /// <summary>聚合查詢。total 為真時不連分組、不 GROUP BY（總計一律獨立重算，設計 §5.5）。</summary>
    public static Built Aggregate(ReportSqlQuery q, ReportSqlCondition? driver, bool total, ISqlDialect dialect)
    {
        var ctx = new Ctx();
        var select = new List<string>();
        var joins = new StringBuilder();
        var groupBy = new List<string>();
        var aliasCounter = 0;

        if (!total)
            for (var i = 0; i < q.Groups.Count; i++)
            {
                var g = q.Groups[i];
                joins.Append($" LEFT JOIN Dynamic_RecordIndex g{i} ON g{i}.RecordId = r.Id AND g{i}.FieldId = {ctx.Add(g.FieldId)}");
                if (!g.Multi) joins.Append($" AND g{i}.Seq = 0");
                var expr = g.Kind switch
                {
                    ReportSqlKind.Text => $"COALESCE(g{i}.TextValue, '')",
                    ReportSqlKind.Ref => $"COALESCE(g{i}.RefKey, '')",
                    ReportSqlKind.Boolean => $"CASE WHEN g{i}.NumberValue IS NULL THEN '' WHEN g{i}.NumberValue = 1 THEN 'true' ELSE 'false' END",
                    ReportSqlKind.Number => $"g{i}.ScaledValue",
                    _ => $"COALESCE({dialect.DateBucket($"g{i}.DateValue", g.Bucket)}, '')",
                };
                select.Add($"{expr} AS k{i}");
                groupBy.Add($"k{i}");
            }

        select.Add("COUNT(*) AS rc");
        var measureAlias = new Dictionary<long, string>();
        for (var j = 0; j < q.Measures.Count; j++)
        {
            var m = q.Measures[j];
            string? a = null;
            if (m.Field is { } f && m.Kind is "sum" or "avg" or "min" or "max")
            {
                if (!measureAlias.TryGetValue(f.FieldId, out a))
                {
                    a = $"m{measureAlias.Count}";
                    measureAlias[f.FieldId] = a;
                    joins.Append($" LEFT JOIN Dynamic_RecordIndex {a} ON {a}.RecordId = r.Id AND {a}.FieldId = {ctx.Add(f.FieldId)} AND {a}.Seq = 0");
                }
            }
            switch (m.Kind)
            {
                case "sum" or "avg":
                    select.Add($"COUNT({a}.ScaledValue) AS n{j}");
                    select.Add($"SUM({a}.ScaledValue / {Split}) AS hi{j}");
                    select.Add($"SUM({a}.ScaledValue % {Split}) AS lo{j}");
                    break;
                case "min" or "max":
                    var fn = m.Kind == "min" ? "MIN" : "MAX";
                    select.Add(m.Field!.Kind == ReportSqlKind.Date ? $"{fn}({a}.DateValue) AS v{j}" : $"{fn}({a}.ScaledValue) AS v{j}");
                    break;
                case "ratio":
                    select.Add($"SUM(CASE WHEN {Predicate(m.Condition!, "r.Id", ctx, ref aliasCounter)} THEN 1 ELSE 0 END) AS hit{j}");
                    break;
            }
        }

        var from = driver is null ? "Dynamic_Record r" : DriverFrom(driver, ctx);
        var where = new StringBuilder();
        AppendScope(where, "r", q.Main, ctx);
        foreach (var f in q.Filters)
            if (!ReferenceEquals(f, driver))
                where.Append(" AND ").Append(Predicate(f, "r.Id", ctx, ref aliasCounter));

        var sql = $"SELECT {string.Join(", ", select)} FROM {from}{joins} WHERE {where}"
            + (groupBy.Count > 0 ? $" GROUP BY {string.Join(", ", groupBy)}" : "");
        return new Built(sql, ctx.Parameters);
    }

    /// <summary>縮小後備載入主記錄用：範圍加精確條件，依 Id 排序（與 ListForReportAsync 一致）。</summary>
    public static Built Narrow(ReportSqlScope scope, IReadOnlyList<ReportSqlCondition> filters, ReportSqlCondition? driver, int limit)
    {
        var ctx = new Ctx();
        var aliasCounter = 0;
        var from = driver is null ? "Dynamic_Record r" : DriverFrom(driver, ctx);
        var where = new StringBuilder();
        AppendScope(where, "r", scope, ctx);
        foreach (var f in filters)
            if (!ReferenceEquals(f, driver))
                where.Append(" AND ").Append(Predicate(f, "r.Id", ctx, ref aliasCounter));
        return new Built($"SELECT r.* FROM {from} WHERE {where} ORDER BY r.Id LIMIT {ctx.Add(limit)}", ctx.Parameters);
    }

    /// <summary>
    /// 縮小後備載入子記錄用：範圍內、連結欄位的 RefKey 指向「縮小後的主記錄」。子模組自己的條件一律不用（左合併語意）。
    /// 主記錄的子查詢與 <see cref="Narrow"/> 同一套條件，參數各自獨立。
    /// </summary>
    public static Built NarrowChildren(
        ReportSqlScope mainScope, IReadOnlyList<ReportSqlCondition> filters, ReportSqlCondition? driver, int limit,
        ReportSqlScope childScope, long linkFieldId)
    {
        var ctx = new Ctx();
        var aliasCounter = 0;
        var from = driver is null ? "Dynamic_Record r" : DriverFrom(driver, ctx);
        var mainWhere = new StringBuilder();
        AppendScope(mainWhere, "r", mainScope, ctx);
        foreach (var f in filters)
            if (!ReferenceEquals(f, driver))
                mainWhere.Append(" AND ").Append(Predicate(f, "r.Id", ctx, ref aliasCounter));
        var childWhere = new StringBuilder();
        AppendScope(childWhere, "c", childScope, ctx);
        var sql = $"SELECT c.* FROM Dynamic_Record c WHERE {childWhere} AND EXISTS (SELECT 1 FROM Dynamic_RecordIndex l WHERE l.RecordId = c.Id AND l.FieldId = {ctx.Add(linkFieldId)} "
            + $"AND l.RefKey IN (SELECT CAST(m.Id AS TEXT) FROM (SELECT r.Id FROM {from} WHERE {mainWhere} ORDER BY r.Id LIMIT {ctx.Add(limit)}) m)) ORDER BY c.Id LIMIT {ctx.Add(limit)}";
        return new Built(sql, ctx.Parameters);
    }
}
