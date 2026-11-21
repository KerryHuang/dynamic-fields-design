namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// MCP 權杖身分的 claim（mcp1a 計畫判斷 9）。只有 McpGateMiddleware（mcp1a-3）會發這些 claim；cookie 登入與 REST 都不發，
/// 所以 <see cref="HttpRequestChannel"/> 讀得到 <see cref="Channel"/>＝<see cref="McpValue"/> 就代表請求真的經過權杖認證。
/// </summary>
public static class McpClaims
{
    public const string Channel = "fw:channel";
    public const string Token = "fw:token";
    public const string Scope = "fw:scope";
    public const string McpValue = "mcp";
}
