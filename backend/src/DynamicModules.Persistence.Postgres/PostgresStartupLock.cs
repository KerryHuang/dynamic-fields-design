using Npgsql;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// 啟動初始化串行（PostgreSQL provider 設計 §5.8）：用一條專用、不進連線池的連線取 session 層級的 <c>pg_advisory_lock</c>，
/// 放開時明確 <c>pg_advisory_unlock</c> 並關閉連線。初始化本身照常用 DbContext，裡面的寫入交易仍照閘門與全庫鎖（不同的鍵）。
/// 目標資料庫必須事先存在（設計 §14 T20）：連不到（3D000）就拒絕啟動，不讓 Migrate 代建，避免多個實例搶著 CREATE DATABASE。
/// </summary>
public sealed class PostgresStartupLock(string connectionString) : IDatabaseStartupLock
{
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({PostgresLockKeys.Startup})", connection);
            // 等前一個實例做完初始化（migration、回填可能超過預設的 30 秒）
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(ct);
            return new Holder(connection);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"PostgreSQL 的目標資料庫「{builder.Database}」不存在：請先建立資料庫再啟動（容器可設 POSTGRES_DB）。" +
                "系統不會自動建立資料庫，避免多個實例同時啟動時搶著建庫", e);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Holder(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({PostgresLockKeys.Startup})", connection);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                // 連線關閉時 session 鎖也會放開；不進連線池，所以不會留在池裡的連線上
                await connection.DisposeAsync();
            }
        }
    }
}
