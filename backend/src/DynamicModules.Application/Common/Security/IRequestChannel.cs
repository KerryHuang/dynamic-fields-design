namespace DynamicModules.Application.Common.Security;

/// <summary>請求從哪個通道進來（MCP 設計 T5、權限軸 §14）。</summary>
public enum RequestChannel { Ui, Mcp }

/// <summary>
/// 這次請求的呼叫通道（mcp1a 計畫判斷 9）。WebAPI 從 <c>HttpContext.User</c> 的 claim 讀，只有 MCP 閘門換上的權杖身分才會是
/// <see cref="RequestChannel.Mcp"/>；讀不到 claim、沒有 HttpContext（背景工作）一律是 <see cref="RequestChannel.Ui"/>。
/// </summary>
public interface IRequestChannel
{
    RequestChannel Channel { get; }
    /// <summary>MCP 通道的權杖 id；畫面通道是 null。</summary>
    long? TokenId { get; }
    /// <summary>MCP 通道的權杖範圍；畫面通道是空清單。</summary>
    IReadOnlyList<string> Scopes { get; }
}

/// <summary>Application 測試與沒有 HTTP 請求的流程用：一律畫面通道。</summary>
public sealed class DefaultRequestChannel : IRequestChannel
{
    public RequestChannel Channel => RequestChannel.Ui;
    public long? TokenId => null;
    public IReadOnlyList<string> Scopes => [];
}
