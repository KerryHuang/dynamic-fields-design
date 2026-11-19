using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

/// <summary>
/// SQLite 專屬的資料庫健康檢查（維運設計 §17.7「provider 專屬的地方」）：WAL 檔大小讀檔案系統，不下 PRAGMA；
/// 完整性檢查用 <c>PRAGMA quick_check</c> 與 <c>PRAGMA foreign_key_check</c>（只在這個類別，交付說明列出）。記憶體資料庫沒有 WAL 檔。
/// </summary>
public sealed class SqliteDatabaseHealthProbe(DynamicDbContext db) : IDatabaseHealthProbe
{
    public bool SupportsFileChecks => true;

    public Task<long?> WalBytesAsync(CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        var source = builder.DataSource;
        if (string.IsNullOrEmpty(source) || builder.Mode == SqliteOpenMode.Memory || source == ":memory:"
            || source.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<long?>(null);
        var wal = new FileInfo(source + "-wal");
        return Task.FromResult<long?>(wal.Exists ? wal.Length : 0);
    }

    public async Task<long> FileIntegrityIssuesAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            long issues = 0;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    if (!string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal)) issues++;
            }
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) issues++;
            }
            return issues;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
