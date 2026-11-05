namespace DynamicModules.Domain.Repositories.Dynamic;

public interface INumberSequenceRepository
{
    /// <summary>取下一個流水號（第一次回 1）；讀取加更新，必須在呼叫端的交易內執行。</summary>
    Task<long> NextAsync(long moduleId, long fieldId, string period, CancellationToken ct);
}
