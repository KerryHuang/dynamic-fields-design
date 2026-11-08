namespace DynamicModules.Persistence.Dynamic.Dialects;

public sealed class SqliteDialect : ISqlDialect
{
    public string Paginate(string selectSql, string orderBy) =>
        $"{selectSql} ORDER BY {orderBy} LIMIT @limit OFFSET @offset";

    public string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    // SQLite 的 LIKE 預設不分大小寫（ASCII）；instr／substr 是二進位比較
    public string ContainsCaseSensitive(string column, string param) => $"instr({column}, {param}) > 0";

    public string StartsWithCaseSensitive(string column, string param) => $"substr({column}, 1, length({param})) = {param}";
}
