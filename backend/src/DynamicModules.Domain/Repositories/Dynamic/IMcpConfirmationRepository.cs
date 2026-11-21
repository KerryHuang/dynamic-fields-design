using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>高風險工具的確認碼（MCP 設計 §6.3）。「未使用」＝<c>UsedUtc</c>、<c>VoidedUtc</c> 皆 null 且 <c>ExpiresUtc &gt; now</c>。</summary>
public interface IMcpConfirmationRepository
{
    void Add(McpConfirmation row);
    Task<McpConfirmation?> FindByHashTrackedAsync(string codeHash, CancellationToken ct);
    /// <summary>某把權杖未使用的確認碼（追蹤，CreatedUtc 遞增）。</summary>
    Task<List<McpConfirmation>> ListOpenTrackedAsync(long tokenId, DateTime nowUtc, CancellationToken ct);
    /// <summary>條件式標記已用（ExecuteUpdate，要在交易型請求內呼叫）；回傳影響列數，1 才算成功。</summary>
    Task<int> TryConsumeAsync(long id, DateTime nowUtc, CancellationToken ct);
    /// <summary>結束（使用、作廢、過期）早於 cutoff 的確認碼，最舊的一批（Id 遞增，追蹤）。</summary>
    Task<List<McpConfirmation>> ListPurgeBatchTrackedAsync(DateTime cutoffUtc, int take, CancellationToken ct);
    void RemoveRange(IEnumerable<McpConfirmation> rows);
}
