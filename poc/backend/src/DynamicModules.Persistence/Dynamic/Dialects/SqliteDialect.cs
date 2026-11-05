namespace DynamicModules.Persistence.Dynamic.Dialects;

public sealed class SqliteDialect : ISqlDialect
{
    public string Paginate(string selectSql, string orderBy) =>
        $"{selectSql} ORDER BY {orderBy} LIMIT @limit OFFSET @offset";

    public string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
