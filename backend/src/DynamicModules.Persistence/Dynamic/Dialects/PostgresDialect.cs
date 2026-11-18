namespace DynamicModules.Persistence.Dynamic.Dialects;

/// <summary>
/// PostgreSQL 方言（PostgreSQL provider 設計 §5.5）。字串欄位在 PostgreSQL 一律 <c>COLLATE "C"</c>（§5.4），
/// 所以 <c>strpos</c>／<c>left</c> 的區分大小寫比較、<c>ILIKE</c> 只對 ASCII 不分大小寫，結果都與 SQLite 相同。
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    public string Paginate(string selectSql, string orderBy) =>
        $"{selectSql} ORDER BY {orderBy} LIMIT @limit OFFSET @offset";

    // standard_conforming_strings 開啟時（預設）ESCAPE '\' 合法，跳脫規則與 SQLite 相同
    public string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public string Bool(bool value) => value ? "TRUE" : "FALSE";

    public string LikeIgnoreCase(string column, string param) => $@"{column} ILIKE {param} ESCAPE '\'";

    public string ContainsCaseSensitive(string column, string param) => $"strpos({column}, {param}) > 0";

    public string StartsWithCaseSensitive(string column, string param) => $"left({column}, length({param})) = {param}";

    /// <summary>
    /// 與 SQLite 版逐字相同的分桶字串（等價測試把關）：ISO 週用 <c>IYYY</c>／<c>IW</c>（週一開始、該週星期四所在的年），
    /// 週次固定兩位數；雙引號內是字面文字。欄位為 NULL 時 <c>to_char</c> 回 NULL。
    /// </summary>
    public string DateBucket(string column, string? bucket) => bucket switch
    {
        null or "day" => $"to_char({column}, 'YYYY-MM-DD')",
        "week" => $"to_char({column}, 'IYYY-\"W\"IW')",
        "month" => $"to_char({column}, 'YYYY-MM')",
        "quarter" => $"to_char({column}, 'YYYY-\"Q\"Q')",
        "year" => $"to_char({column}, 'YYYY')",
        _ => throw new ArgumentException($"未知的分桶：{bucket}", nameof(bucket)),
    };

    public string IndexHint(string index) => "";

    public string OrderedJoin() => "JOIN";
}
