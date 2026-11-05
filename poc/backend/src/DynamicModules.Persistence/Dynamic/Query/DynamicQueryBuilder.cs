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
/// </summary>
public static class DynamicQueryBuilder
{
    private const string Escape = @" ESCAPE '\'";

    private sealed class Ctx(ISqlDialect dialect)
    {
        public ISqlDialect Dialect { get; } = dialect;
        public DynamicParameters Parameters { get; } = new();
        public int Count { get; set; }
    }

    public static (string countSql, string pageSql, DynamicParameters parameters) Build(RecordQuery q, ISqlDialect dialect)
    {
        var ctx = new Ctx(dialect);
        var where = new StringBuilder("r.ModuleId = @m AND r.DeleteFlag = 0");
        ctx.Parameters.Add("m", q.ModuleId);
        if (q.OwnerUserIds is not null)
        {
            if (q.OwnerUserIds.Count == 0) where.Append(" AND 1 = 0");
            else
            {
                var names = q.OwnerUserIds.Select((id, i) => { ctx.Parameters.Add($"o{i}", id); return $"@o{i}"; });
                where.Append(" AND r.OwnerUserId IN (").Append(string.Join(", ", names)).Append(')');
            }
        }
        if (q.Filter is not null) where.Append(" AND ").Append(Node(q.Filter, ctx, q.Today));
        if (q.Keyword is not null)
        {
            where.Append(" AND r.Title LIKE @q").Append(Escape);
            ctx.Parameters.Add("q", $"%{dialect.EscapeLike(q.Keyword)}%");
        }

        var joins = new StringBuilder();
        var orderBy = new StringBuilder();
        for (var k = 0; k < q.Sorts.Count; k++)
        {
            var sort = q.Sorts[k];
            joins.Append($" LEFT JOIN Dynamic_RecordIndex s{k} ON s{k}.RecordId = r.Id AND s{k}.FieldId = @s{k} AND s{k}.Seq = 0");
            ctx.Parameters.Add($"s{k}", sort.Field.Id);
            string expr;
            if (sort.Field.Type == FieldType.Enum)
            {
                joins.Append($" LEFT JOIN Dynamic_FieldOption o{k} ON o{k}.Id = s{k}.NumberValue");
                expr = $"o{k}.Sequence";
            }
            else
            {
                expr = $"s{k}.{Column(sort.Field.Type)}";
            }
            orderBy.Append($"CASE WHEN {expr} IS NULL THEN 1 ELSE 0 END, {expr} {(sort.Descending ? "DESC" : "ASC")}, ");
        }
        orderBy.Append("r.Id DESC");

        ctx.Parameters.Add("offset", (q.Page - 1) * q.Size);
        ctx.Parameters.Add("limit", q.Size);

        var countSql = $"SELECT COUNT(*) FROM Dynamic_Record r WHERE {where}";
        var pageSql = dialect.Paginate($"SELECT r.* FROM Dynamic_Record r{joins} WHERE {where}", orderBy.ToString());
        return (countSql, pageSql, ctx.Parameters);
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
        var col = $"i.{Column(c.Field.Type)}";
        ctx.Parameters.Add($"f{n}", c.Field.Id);

        string Val(int idx) => AddValue(ctx, $"v{n}_{idx}", c.Field.Type, c.Values[idx]);
        // 不變式（R8）：每個條件都是非相關的 r.Id [NOT] IN (SELECT RecordId …) 子查詢；
        // 否定形式（ne、notIn、isEmpty）會包含沒有索引列的記錄；RecordId 為 NOT NULL，所以 NOT IN 不會踩到 NULL。
        string InSubquery(string? cmp, bool negate) =>
            $"r.Id {(negate ? "NOT IN" : "IN")} (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f{n}"
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
            case FilterOperator.Contains:
                ctx.Parameters.Add($"v{n}_0", $"%{ctx.Dialect.EscapeLike(c.Values[0].TextValue!)}%");
                return InSubquery($"{col} LIKE @v{n}_0{Escape}", false);
            case FilterOperator.StartsWith:
                ctx.Parameters.Add($"v{n}_0", $"{ctx.Dialect.EscapeLike(c.Values[0].TextValue!)}%");
                return InSubquery($"{col} LIKE @v{n}_0{Escape}", false);
            case FilterOperator.WithinNextDays:
            case FilterOperator.WithinLastDays:
                var days = c.Days!.Value;
                var start = c.Op == FilterOperator.WithinNextDays ? today : today.AddDays(-days);
                var end = c.Op == FilterOperator.WithinNextDays ? today.AddDays(days) : today;
                // 含頭尾整天：>= 起日 00:00 且 < 迄日隔天 00:00
                ctx.Parameters.Add($"v{n}_0", start.ToDateTime(TimeOnly.MinValue));
                ctx.Parameters.Add($"v{n}_1", end.AddDays(1).ToDateTime(TimeOnly.MinValue));
                return InSubquery($"{col} >= @v{n}_0 AND {col} < @v{n}_1", false);
            case FilterOperator.IsEmpty: return InSubquery(null, true);
            case FilterOperator.IsNotEmpty: return InSubquery(null, false);
            default: throw new InvalidOperationException($"未支援的運算子 {c.Op}");
        }
    }

    /// <summary>依欄位型別取對應索引欄的值加入參數，回傳參數名稱（含 @）。</summary>
    private static string AddValue(Ctx ctx, string name, FieldType type, IndexValue v)
    {
        object? value = Column(type) switch
        {
            "TextValue" => v.TextValue,
            "NumberValue" => v.NumberValue,
            "DateValue" => v.DateValue,
            _ => v.RefKey
        };
        ctx.Parameters.Add(name, value);
        return "@" + name;
    }

    /// <summary>型別 → 索引欄（白名單常數）；enum／multienum 比較用 TextValue（R1）。</summary>
    private static string Column(FieldType type) => type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Boolean => "NumberValue",
        FieldType.Date or FieldType.DateTime => "DateValue",
        FieldType.User or FieldType.Reference => "RefKey",
        _ => "TextValue"
    };
}
