using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.Persistence;

/// <summary>
/// 每條 SQLite 連線開啟時套用 <c>PRAGMA mmap_size</c>（這個設定是逐連線的，不會跟著檔案走）。
/// 大型資料庫的索引查詢可快 30% 至 45%（儲存負載報告 §5），代價是用掉位址空間與作業系統的頁面快取，所以是設定項、預設關閉。
/// </summary>
public sealed class SqliteMmapInterceptor(long bytes) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA mmap_size = {bytes}";
        cmd.ExecuteNonQuery();
    }
}
