namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>SaveChanges；交易由 TransactionBehavior 負責，同一交易內可多次呼叫。</summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct);

    /// <summary>丟掉追蹤中、還沒存檔的修改（失敗的請求回滾之後、同一個 scope 還要另寫一筆時用）。</summary>
    void Discard();
}
