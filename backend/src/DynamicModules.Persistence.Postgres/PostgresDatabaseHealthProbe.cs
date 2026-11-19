using DynamicModules.Application.Dynamic.OpsService.Health;

namespace DynamicModules.Persistence.Postgres;

/// <summary>PostgreSQL 的資料庫健康檢查：O2 沒有檔案層檢查（WAL 檔大小回不適用；amcheck 等延到 O4，O2-U13），只做產品層的共用主檔檢查。</summary>
public sealed class PostgresDatabaseHealthProbe : IDatabaseHealthProbe
{
    public bool SupportsFileChecks => false;

    public Task<long?> WalBytesAsync(CancellationToken ct) => Task.FromResult<long?>(null);

    public Task<long> FileIntegrityIssuesAsync(CancellationToken ct) => Task.FromResult(0L);
}
