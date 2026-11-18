using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DynamicModules.Persistence;

/// <summary>部署設定 <c>Database:Provider</c> 選的資料庫種類（PostgreSQL provider 設計 §5.1）。</summary>
public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public static class DatabaseProviders
{
    public const string SettingKey = "Database:Provider";

    /// <summary>
    /// 讀 <c>Database:Provider</c>：沒設是 <see cref="DatabaseProvider.Sqlite"/>；只接受 Sqlite、Postgres（不分大小寫），
    /// 其他值丟例外讓站台拒絕啟動。不從連線字串猜 provider（設計 §14 T1）。
    /// </summary>
    public static DatabaseProvider Read(IConfiguration configuration)
    {
        var raw = configuration[SettingKey];
        if (string.IsNullOrWhiteSpace(raw)) return DatabaseProvider.Sqlite;
        return raw.Trim().ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" => DatabaseProvider.Postgres,
            _ => throw new InvalidOperationException($"{SettingKey} 的值「{raw}」不支援，只能是 Sqlite 或 Postgres"),
        };
    }

    /// <summary>
    /// 資料庫檔案所在的目錄（建目錄、DataProtection 金鑰、預設附件目錄都依它）：
    /// SQLite 檔案型資料庫回檔案目錄；記憶體資料庫、file: URI 與 PostgreSQL 回 null。
    /// 連線字串須已經過 <see cref="SqliteConnectionStrings.ResolveDataSource"/>（相對路徑已解析成絕對路徑）。
    /// </summary>
    public static string? FileDirectory(DatabaseProvider provider, string connectionString)
    {
        if (provider != DatabaseProvider.Sqlite) return null;
        var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return Path.IsPathRooted(source) ? Path.GetDirectoryName(source) : null;
    }
}
