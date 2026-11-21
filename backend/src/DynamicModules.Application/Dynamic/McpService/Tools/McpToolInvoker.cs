using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Common.Security;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>呼叫端（SDK）提供的不可信資訊：client 名稱與版本、<c>X-Formwright-Job</c> 標頭；只進稽核摘要。</summary>
public sealed record McpCallContext(string? ClientName = null, string? ClientVersion = null, string? Job = null);

/// <summary>
/// 工具執行器（mcp1a 計畫判斷 19）：通道 → 找工具 → 範圍 → 執行 → 格式化。回傳格式化好的結果，SDK 介面只負責包成 <c>CallToolResult</c>。
/// 不認得的工具回 <c>unknown_tool</c> 錯誤結果（模型可修正），不用 JSON-RPC error。
/// </summary>
public sealed class McpToolInvoker(
    IEnumerable<IMcpTool> tools, IRequestChannel channel, McpPolicies policies, IMessageCatalog messages, RequestLocale locale,
    ILogger<McpToolInvoker> log)
{
    private readonly IReadOnlyList<IMcpTool> _tools = tools.ToList();

    /// <summary><c>tools/list</c>：權杖範圍涵蓋的工具（範圍是 null 的 <c>whoami</c> 一律列），依名稱 Ordinal 排序。</summary>
    public IReadOnlyList<IMcpTool> ListFor(IReadOnlyList<string> scopes) =>
        _tools.Where(t => t.Descriptor.Scope is null || scopes.Contains(t.Descriptor.Scope, StringComparer.Ordinal))
            .OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal).ToList();

    public async Task<McpFormattedResult> InvokeAsync(string name, JsonElement? arguments, McpCallContext context, CancellationToken ct)
    {
        // 通道不是 MCP 是程式錯誤（只有 /mcp 閘門換上的權杖身分會進來），fail closed
        if (channel.Channel != RequestChannel.Mcp) throw new InvalidOperationException("工具執行器只能在 MCP 通道使用");
        var lc = locale.Current;
        string Text(MessageRef m) => m.Resolve(messages, lc);

        var tool = _tools.FirstOrDefault(t => string.Equals(t.Descriptor.Name, name, StringComparison.Ordinal));
        if (tool is null) return McpResultFormatter.Error(McpErrorCodes.UnknownTool, Text(McpMessages.UnknownTool));
        if (tool.Descriptor.Scope is { } scope && !channel.Scopes.Contains(scope, StringComparer.Ordinal))
            return McpResultFormatter.Error(McpErrorCodes.ScopeDenied, Text(McpMessages.ScopeDenied));

        var args = arguments is { ValueKind: not JsonValueKind.Undefined } a ? a : EmptyObject;
        var snapshot = await policies.ReadAsync(ct);
        var outcome = await tool.RunAsync(new McpToolCall(args, snapshot, lc.Locale), ct);
        return Format(tool.Descriptor.Name, Mapped(outcome), Text);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>handler 回的失敗對照成工具錯誤（設計 §6.5）：Forbidden 一律「沒有權限」、不透露存在；Invalid 帶欄位；Conflict 是 stale。</summary>
    internal static McpToolOutcome Mapped(McpToolOutcome outcome)
    {
        if (outcome.Failure is not { } r) return outcome;
        if (r.IsForbidden || r.IsUnauthorized)
            return outcome with { ErrorCode = McpErrorCodes.Forbidden, ErrorMessage = MessageRef.Of(MessageText.Forbidden) };
        if (r.IsNotFound)
            return outcome with { ErrorCode = McpErrorCodes.NotFound, ErrorMessage = MessageRef.Of(MessageText.ResourceNotFound) };
        if (r.IsConflict)
            return outcome with { ErrorCode = McpErrorCodes.Stale, ErrorMessage = MessageRef.Of(MessageText.Stale) };
        // 其餘（Invalid、Unprocessable、TooManyRequests）照 handler 的訊息
        return outcome with { ErrorCode = r.IsTooManyRequests ? McpErrorCodes.RateLimited : McpErrorCodes.Invalid };
    }

    private McpFormattedResult Format(string tool, McpToolOutcome outcome, Func<MessageRef, string> text)
    {
        if (!outcome.IsError) return McpResultFormatter.Success(tool, outcome.Meta, outcome.Data);
        var fields = outcome.Fields?.ToDictionary(kv => kv.Key, kv => text(kv.Value))
            ?? (outcome.Failure is { IsInvalid: true } f
                ? f.ErrorsIn(messages, locale.Current).ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value))
                : null);
        var message = outcome.ErrorMessage is { } m ? text(m)
            : outcome.Failure?.MessageIn(messages, locale.Current) ?? fields?.Values.FirstOrDefault() ?? text(McpMessages.InternalError);
        if (outcome.ErrorCode is null) log.LogError("MCP 工具 {Tool} 回了沒有錯誤代碼的錯誤結果", tool);
        return McpResultFormatter.Error(outcome.ErrorCode ?? McpErrorCodes.InternalError, message, fields, outcome.RetryAfterSeconds);
    }
}
