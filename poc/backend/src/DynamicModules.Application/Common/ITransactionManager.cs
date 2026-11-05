namespace DynamicModules.Application.Common;

public interface ITransactionManager
{
    Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct);
}

public interface ITransactionScopeHandle : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}
