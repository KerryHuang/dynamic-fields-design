using Microsoft.Data.Sqlite;

namespace DynamicModules.Persistence;

public static class SqliteConnectionStrings
{
    /// <summary>檔案型 Data Source 以 contentRoot 解析成絕對路徑；記憶體與 file: URI 原樣返回。</summary>
    public static string ResolveDataSource(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || source.StartsWith("file:")
            || builder.Mode == SqliteOpenMode.Memory)
            return connectionString;

        builder.DataSource = Path.GetFullPath(source, contentRoot);
        return builder.ConnectionString;
    }
}
