using System.Globalization;
using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// 從 <c>HttpContext.User</c> 的 claim 讀呼叫通道（mcp1a 計畫判斷 9）：<see cref="McpClaims.Channel"/>＝mcp 才是 MCP。
/// 沒有 HttpContext（背景工作）或沒有這個 claim 一律是畫面通道。讀 claim、不放 DI scope 的可變物件：SDK 執行工具用哪個 scope 沒有保證，
/// claim 跟著 HttpContext.User 走（同 ClaimsCurrentUser）。
/// </summary>
public sealed class HttpRequestChannel(IHttpContextAccessor accessor) : IRequestChannel
{
    public RequestChannel Channel =>
        accessor.HttpContext?.User.FindFirst(McpClaims.Channel)?.Value == McpClaims.McpValue ? RequestChannel.Mcp : RequestChannel.Ui;

    public long? TokenId =>
        Channel == RequestChannel.Mcp
        && long.TryParse(accessor.HttpContext!.User.FindFirst(McpClaims.Token)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id : null;

    public IReadOnlyList<string> Scopes =>
        Channel == RequestChannel.Mcp ? accessor.HttpContext!.User.FindAll(McpClaims.Scope).Select(c => c.Value).ToList() : [];
}
