using System.Data;
using System.Text;
using Dapper;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;

namespace DynamicModules.Persistence.Dynamic.Query;

/// <summary>
/// 把條件 AST 與排序轉成參數化 SQL。SQL 只由常數與白名單片段組成；
/// 欄位 id、值、模組 id 一律走參數（@f0、@v0_1 …），不拼進字串。
/// 識別字一律加雙引號、布林與不分大小寫的 LIKE 走方言，SQLite 與 PostgreSQL 共用同一份字串（PostgreSQL provider 設計 §5.5）。
/// </summary>
public static class DynamicQueryBuilder
{
    private sealed class Ctx(ISqlDialect dialect, bool caseSensitiveText = false)
    {
        public ISqlDialect Dialect { get; } = dialect;

        /// <summary>跨記錄驗證的聚合要和規則條件的記憶體求值（序數比較）同一個結果，所以文字的包含、開頭是區分大小寫；列表篩選維持不分。</summary>
        public bool CaseSensitiveText { get; } = caseSensitiveText;
        public DynamicParameters Parameters { get; } = new();
        public int Count { get; set; }
    }

    public static (string countSql, string pageSql, DynamicParameters parameters) Build(RecordQuery q, ISqlDialect dialect)
    {
        var ctx = new Ctx(dialect);
        var where = new StringBuilder($"r.\"ModuleId\" = @m AND r.\"DeleteFlag\" = {dialect.Bool(false)}");
        ctx.Parameters.Add("m", q.ModuleId);
        if (q.OwnerUserIds is not null)
        {
            if (q.OwnerUserIds.Count == 0) where.Append(" AND 1 = 0");
            else
            {
                var names = q.OwnerUserIds.Select((id, i) => { ctx.Parameters.Add($"o{i}", id); return $"@o{i}"; });
                where.Append(" AND r.\"OwnerUserId\" IN (").Append(string.Join(", ", names)).Append(')');
            }
        }
        if (q.Filter is not null) where.Append(" AND ").Append(Node(q.Filter, ctx, q.Today));
        if (q.Approval is { } approval)
        {
            // 簽核中是「有進行中實例」這個事實（簽核設計 §13.4.1）：非相關子查詢，走 (ModuleId, Status) 索引；RecordId 為 NOT NULL，NOT IN 不會踩到 NULL
            where.Append($" AND r.\"Id\" {(approval == ApprovalListFilter.Pending ? "IN" : "NOT IN")} "
                + "(SELECT a.\"RecordId\" FROM \"Dynamic_ApprovalInstance\" a WHERE a.\"ModuleId\" = @m AND a.\"Status\" = @apStatus)");
            ctx.Parameters.Add("apStatus", ApprovalInstanceStatus.Pending);
        }
        if (q.Keyword is not null)
        {
            where.Append(" AND ").Append(dialect.LikeIgnoreCase("r.\"Title\"", "@q"));
            ctx.Parameters.Add("q", $"%{dialect.EscapeLike(q.Keyword)}%");
        }

        var joins = new StringBuilder();
        var orderBy = new StringBuilder();
        for (var k = 0; k < q.Sorts.Count; k++)
        {
            var sort = q.Sorts[k];
            if (sort.ByTitle)
            {
                orderBy.Append($"r.\"Title\" {(sort.Descending ? "DESC" : "ASC")}, ");   // Title 必填，沒有 NULL
                continue;
            }
            joins.Append($" LEFT JOIN \"Dynamic_RecordIndex\" s{k} ON s{k}.\"RecordId\" = r.\"Id\" AND s{k}.\"FieldId\" = @s{k} AND s{k}.\"Seq\" = 0");
            ctx.Parameters.Add($"s{k}", sort.Field.Id);
            string expr;
            if (sort.Field.Type == FieldType.Enum)
            {
                joins.Append($" LEFT JOIN \"Dynamic_FieldOption\" o{k} ON o{k}.\"Id\" = s{k}.\"NumberValue\"");
                expr = $"o{k}.\"Sequence\"";
            }
            else
            {
                expr = $"s{k}.\"{Column(sort.Field)}\"";
            }
            orderBy.Append($"CASE WHEN {expr} IS NULL THEN 1 ELSE 0 END, {expr} {(sort.Descending ? "DESC" : "ASC")}, ");
        }
        orderBy.Append("r.\"Id\" DESC");

        ctx.Parameters.Add("offset", (q.Page - 1) * q.Size);
        ctx.Parameters.Add("limit", q.Size);

        var countSql = $"SELECT COUNT(*) FROM \"Dynamic_Record\" r WHERE {where}";
        var pageSql = dialect.Paginate($"SELECT r.* FROM \"Dynamic_Record\" r{joins} WHERE {where}", orderBy.ToString());
        return (countSql, pageSql, ctx.Parameters);
    }

    /// <summary>
    /// 跨記錄驗證的聚合 SQL（設計 §5.2）：組鍵與條件走和列表篩選相同的參數化子查詢；加總用索引時算好的 ScaledValue（精確整數）。
    /// 加總與常數一律 CAST 成 BIGINT，兩種資料庫都回 64 位元整數（PostgreSQL 的 SUM(bigint) 回 numeric）。
    /// </summary>
    public static (string sql, DynamicParameters parameters) BuildAggregate(AggregateQuery q, ISqlDialect dialect)
    {
        var ctx = new Ctx(dialect, caseSensitiveText: true);
        ctx.Parameters.Add("m", q.ModuleId);
        var where = new StringBuilder($"r.\"ModuleId\" = @m AND r.\"DeleteFlag\" = {dialect.Bool(false)}");
        for (var k = 0; k < q.Group.Count; k++)
        {
            var (field, value) = (q.Group[k].Field, q.Group[k].Value);
            ctx.Parameters.Add($"g{k}", field.Id);
            if (value is null)
            {
                where.Append($" AND r.\"Id\" NOT IN (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @g{k})");
                continue;
            }
            where.Append($" AND r.\"Id\" IN (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @g{k} AND i.\"{Column(field)}\" = {AddValue(ctx, $"gv{k}", field, value)})");
        }
        if (q.Where is not null) where.Append(" AND ").Append(Node(q.Where, ctx, q.Today));

        if (q.SumField is null)
            return ($"SELECT COUNT(*) AS \"Count\", CAST(0 AS BIGINT) AS \"ScaledSum\", CAST(0 AS BIGINT) AS \"MissingScaled\" FROM \"Dynamic_Record\" r WHERE {where}", ctx.Parameters);
        ctx.Parameters.Add("sum", q.SumField.Id);
        return ($"SELECT COUNT(*) AS \"Count\", CAST(COALESCE(SUM(s.\"ScaledValue\"), 0) AS BIGINT) AS \"ScaledSum\", "
                + "CAST(COALESCE(SUM(CASE WHEN s.\"RecordId\" IS NOT NULL AND s.\"ScaledValue\" IS NULL THEN 1 ELSE 0 END), 0) AS BIGINT) AS \"MissingScaled\" "
                + $"FROM \"Dynamic_Record\" r LEFT JOIN \"Dynamic_RecordIndex\" s ON s.\"RecordId\" = r.\"Id\" AND s.\"FieldId\" = @sum AND s.\"Seq\" = 0 WHERE {where}",
            ctx.Parameters);
    }

    private static string Node(ConditionNode node, Ctx ctx, DateOnly today) => node switch
    {
        ConditionGroup g => "(" + string.Join(g.All ? " AND " : " OR ", g.Children.Select(c => Node(c, ctx, today))) + ")",
        FieldCondition c => Condition(c, ctx, today),
        _ => throw new InvalidOperationException("未知的條件節點")
    };

    private static string Condition(FieldCondition c, Ctx ctx, DateOnly today)
    {
        var n = ctx.Count++;
        var col = $"i.\"{Column(c.Field)}\"";
        ctx.Parameters.Add($"f{n}", c.Field.Id);

        string Val(int idx) => AddValue(ctx, $"v{n}_{idx}", c.Field, c.Values[idx]);
        // 不變式（R8）：每個條件都是非相關的 r.Id [NOT] IN (SELECT RecordId …) 子查詢；
        // 否定形式（ne、notIn、isEmpty）會包含沒有索引列的記錄；RecordId 為 NOT NULL，所以 NOT IN 不會踩到 NULL。
        string InSubquery(string? cmp, bool negate) =>
            $"r.\"Id\" {(negate ? "NOT IN" : "IN")} (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @f{n}"
            + (cmp is null ? "" : " AND " + cmp) + ")";
        string InList() => $"{col} IN ({string.Join(", ", c.Values.Select((_, idx) => Val(idx)))})";

        switch (c.Op)
        {
            case FilterOperator.Eq: return InSubquery($"{col} = {Val(0)}", false);
            case FilterOperator.Ne: return InSubquery($"{col} = {Val(0)}", true);
            case FilterOperator.In: return InSubquery(InList(), false);
            case FilterOperator.NotIn: return InSubquery(InList(), true);
            case FilterOperator.Gt: return InSubquery($"{col} > {Val(0)}", false);
            case FilterOperator.Gte: return InSubquery($"{col} >= {Val(0)}", false);
            case FilterOperator.Lt: return InSubquery($"{col} < {Val(0)}", false);
            case FilterOperator.Lte: return InSubquery($"{col} <= {Val(0)}", false);
            case FilterOperator.Between: return InSubquery($"{col} >= {Val(0)} AND {col} <= {Val(1)}", false);
            case FilterOperator.Contains when ctx.CaseSensitiveText:
                ctx.Parameters.Add($"v{n}_0", c.Values[0].TextValue!);
                return InSubquery(ctx.Dialect.ContainsCaseSensitive(col, $"@v{n}_0"), false);
            case FilterOperator.StartsWith when ctx.CaseSensitiveText:
                ctx.Parameters.Add($"v{n}_0", c.Values[0].TextValue!);
                return InSubquery(ctx.Dialect.StartsWithCaseSensitive(col, $"@v{n}_0"), false);
            case FilterOperator.Contains:
                ctx.Parameters.Add($"v{n}_0", $"%{ctx.Dialect.EscapeLike(c.Values[0].TextValue!)}%");
                return InSubquery(ctx.Dialect.LikeIgnoreCase(col, $"@v{n}_0"), false);
            case FilterOperator.StartsWith:
                ctx.Parameters.Add($"v{n}_0", $"{ctx.Dialect.EscapeLike(c.Values[0].TextValue!)}%");
                return InSubquery(ctx.Dialect.LikeIgnoreCase(col, $"@v{n}_0"), false);
            case FilterOperator.WithinNextDays:
            case FilterOperator.WithinLastDays:
                var days = c.Days!.Value;
                var start = c.Op == FilterOperator.WithinNextDays ? today : today.AddDays(-days);
                var end = c.Op == FilterOperator.WithinNextDays ? today.AddDays(days) : today;
                // 含頭尾整天：>= 起日 00:00 且 < 迄日隔天 00:00
                AddDateTime(ctx, $"v{n}_0", start.ToDateTime(TimeOnly.MinValue));
                AddDateTime(ctx, $"v{n}_1", end.AddDays(1).ToDateTime(TimeOnly.MinValue));
                return InSubquery($"{col} >= @v{n}_0 AND {col} < @v{n}_1", false);
            case FilterOperator.BeforeToday:
                // 嚴格早於業務日：< 今天 00:00；沒有索引列（空值）自然不符合
                AddDateTime(ctx, $"v{n}_0", today.ToDateTime(TimeOnly.MinValue));
                return InSubquery($"{col} < @v{n}_0", false);
            case FilterOperator.IsEmpty: return InSubquery(null, true);
            case FilterOperator.IsNotEmpty: return InSubquery(null, false);
            default: throw new InvalidOperationException($"未支援的運算子 {c.Op}");
        }
    }

    /// <summary>依欄位型別取對應索引欄的值加入參數，回傳參數名稱（含 @）。</summary>
    private static string AddValue(Ctx ctx, string name, Field field, IndexValue v)
    {
        switch (Column(field))
        {
            case "TextValue": ctx.Parameters.Add(name, v.TextValue); break;
            case "NumberValue": ctx.Parameters.Add(name, v.NumberValue); break;
            case "DateValue": AddDateTime(ctx, name, v.DateValue); break;
            default: ctx.Parameters.Add(name, v.RefKey); break;
        }
        return "@" + name;
    }

    /// <summary>
    /// 日期參數明確指定 DateTime2：Dapper 預設給 DbType.DateTime，Npgsql 會把它當 timestamptz 而拒收 Kind=Unspecified 的牆上時間；
    /// Microsoft.Data.Sqlite 對兩種 DbType 寫成相同的文字。不用 SqlMapper.AddTypeMap（全域設定，設計 §14 T19）。
    /// </summary>
    private static void AddDateTime(Ctx ctx, string name, DateTime? value) => ctx.Parameters.Add(name, value, DbType.DateTime2);

    /// <summary>欄位 → 索引欄名（白名單常數，不含引號）；enum／multienum 比較用 TextValue（R1）。公式與彙總的索引列依 ResultType 存在 DateValue 或 NumberValue。</summary>
    private static string Column(Field field) => field.Type switch
    {
        FieldType.Formula or FieldType.Rollup => field.Config.ResultType == "date" ? "DateValue" : "NumberValue",
        FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Boolean => "NumberValue",
        FieldType.Date or FieldType.DateTime => "DateValue",
        FieldType.User or FieldType.Reference => "RefKey",
        _ => "TextValue"
    };
}
