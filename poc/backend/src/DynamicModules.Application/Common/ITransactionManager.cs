namespace DynamicModules.Application.Common;

public interface ITransactionManager
{
    Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct);

    /// <summary>目前是否已在交易內（巢狀的請求看得到外層開的交易）。</summary>
    bool IsActive { get; }
}

public interface ITransactionScopeHandle : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}
