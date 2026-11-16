namespace DynamicModules.Application.Common;

/// <summary>
/// 在目前請求的交易內開一個 savepoint，讓一段多步驟的寫入可以整段回滾而不影響交易裡先前已儲存的內容。
/// 回滾後 change tracker 會被清空，所以呼叫端必須在自己的 SaveChanges 之後才開 savepoint。
/// </summary>
public interface IWorkSavepoints
{
    Task<IWorkSavepoint> CreateAsync(CancellationToken ct);
}

public interface IWorkSavepoint : IAsyncDisposable
{
    /// <summary>回到開 savepoint 的時間點，並清掉 change tracker。</summary>
    Task RollbackAsync(CancellationToken ct);

    /// <summary>保留這段寫入（併入外層交易）。</summary>
    Task ReleaseAsync(CancellationToken ct);
}
