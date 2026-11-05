namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>SaveChanges；交易由 TransactionBehavior 負責，同一交易內可多次呼叫。</summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct);
}
