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

    public string DateBucket(string column, string? bucket)
    {
        // ISO 週：該週的星期四決定年份與週次（週一到週日為一週）；'-3 days' 再 'weekday 4' 一定落在同一週的星期四
        var thursday = $"date({column}, '-3 days', 'weekday 4')";
        return bucket switch
        {
            null or "day" => $"date({column})",
            "week" => $"CASE WHEN {column} IS NULL THEN NULL ELSE strftime('%Y', {thursday}) || '-W' || printf('%02d', (CAST(strftime('%j', {thursday}) AS INTEGER) - 1) / 7 + 1) END",
            "month" => $"strftime('%Y-%m', {column})",
            "quarter" => $"CASE WHEN {column} IS NULL THEN NULL ELSE strftime('%Y', {column}) || '-Q' || ((CAST(strftime('%m', {column}) AS INTEGER) - 1) / 3 + 1) END",
            "year" => $"strftime('%Y', {column})",
            _ => throw new ArgumentException($"未知的分桶：{bucket}", nameof(bucket)),
        };
    }
}
