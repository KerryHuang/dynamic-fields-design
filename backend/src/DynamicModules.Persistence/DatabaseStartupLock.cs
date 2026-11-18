namespace DynamicModules.Persistence;

/// <summary>
/// 啟動初始化（migrate、種子、admin 引導、索引回填、主檔檢查）的跨實例串行鎖（PostgreSQL provider 設計 §5.8、§14 T15）。
/// SQLite 不需要（同一個檔案的寫入本來就序列化），註冊不做事的版本；PostgreSQL 用 session advisory lock。
/// </summary>
public interface IDatabaseStartupLock
{
    /// <summary>取得鎖；回傳的物件 Dispose 時放開。</summary>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
}

public sealed class NoDatabaseStartupLock : IDatabaseStartupLock
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct) => Task.FromResult<IAsyncDisposable>(Released.Instance);

    private sealed class Released : IAsyncDisposable
    {
        public static readonly Released Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
