using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>個人存取權杖（MCP 設計 §13.3）。「有效」＝未撤銷且 <c>ExpiresUtc &gt; now</c>。</summary>
public interface IMcpTokenRepository
{
    void Add(McpToken token);
    /// <summary>依雜湊等值查（不追蹤）；呼叫端再以 <c>McpSecrets.HashEquals</c> 比對。</summary>
    Task<McpToken?> FindByHashAsync(string tokenHash, CancellationToken ct);
    Task<McpToken?> GetTrackedAsync(long id, CancellationToken ct);
    /// <summary>某使用者的全部權杖（不追蹤，Id 遞減）。</summary>
    Task<List<McpToken>> ListByUserAsync(string userId, CancellationToken ct);
    /// <summary>管理員列表；<paramref name="userId"/> 是 null 列全部（不追蹤，Id 遞減）。</summary>
    Task<List<McpToken>> ListAsync(string? userId, CancellationToken ct);
    Task<int> CountActiveAsync(string userId, DateTime nowUtc, CancellationToken ct);
    Task<List<McpToken>> ListActiveTrackedAsync(string userId, DateTime nowUtc, CancellationToken ct);
    /// <summary>過期或撤銷早於 cutoff 的權杖，最舊的一批（Id 遞增，追蹤）。</summary>
    Task<List<McpToken>> ListPurgeBatchTrackedAsync(DateTime cutoffUtc, int take, CancellationToken ct);
    void RemoveRange(IEnumerable<McpToken> tokens);
}
