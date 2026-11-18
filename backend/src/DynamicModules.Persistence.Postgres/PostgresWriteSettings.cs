using Microsoft.Extensions.Configuration;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的寫入序列化設定（PostgreSQL provider 設計 §5.6）：<c>Database:LockTimeoutSeconds</c>（預設 30）同時是
/// <see cref="WriteGate"/> 排隊與全庫 advisory lock 的等待上限。這是部署設定，不是業務政策；0 或負數是不設上限（與 SQLite 的 Default Timeout 相同）。
/// </summary>
public sealed class PostgresWriteSettings
{
    public const string LockTimeoutKey = "Database:LockTimeoutSeconds";
    public const int DefaultLockTimeoutSeconds = 30;

    public PostgresWriteSettings(int lockTimeoutSeconds)
    {
        LockTimeoutSeconds = lockTimeoutSeconds;
        LockTimeout = lockTimeoutSeconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(lockTimeoutSeconds);
        WriteLock = new PostgresWriteLockInterceptor(this);
        SaveChangesGate = new PostgresSaveChangesGateInterceptor(this);
    }

    public int LockTimeoutSeconds { get; }

    /// <summary>閘門排隊的上限；不設上限時是 <see cref="Timeout.InfiniteTimeSpan"/>。</summary>
    public TimeSpan LockTimeout { get; }

    /// <summary>全庫寫入鎖的交易攔截器；每份設定一個實例，所有 DbContext 共用。</summary>
    public PostgresWriteLockInterceptor WriteLock { get; }

    /// <summary><c>AutoTransactionBehavior=Always</c> 與 SaveChanges 自己開交易時的閘門。</summary>
    public PostgresSaveChangesGateInterceptor SaveChangesGate { get; }

    /// <summary>讀 <c>Database:LockTimeoutSeconds</c>；沒設用預設值，不是整數就丟例外讓站台拒絕啟動。</summary>
    public static PostgresWriteSettings From(IConfiguration configuration)
    {
        var raw = configuration[LockTimeoutKey];
        if (string.IsNullOrWhiteSpace(raw)) return new(DefaultLockTimeoutSeconds);
        return int.TryParse(raw.Trim(), out var seconds)
            ? new(seconds)
            : throw new InvalidOperationException($"{LockTimeoutKey} 的值「{raw}」不是整數（秒）");
    }
}
