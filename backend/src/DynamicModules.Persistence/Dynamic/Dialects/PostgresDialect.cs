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

    /// <summary>報表 SQL 路徑在 pg1 才支援 PostgreSQL；pg0 的報表一律走記憶體路徑（設計 §5.9），這裡先擋住誤用。</summary>
    public string DateBucket(string column, string? bucket) =>
        throw new NotSupportedException("PostgreSQL 的日期分桶在 pg1 才提供；報表在 PostgreSQL 上走記憶體路徑");
}
