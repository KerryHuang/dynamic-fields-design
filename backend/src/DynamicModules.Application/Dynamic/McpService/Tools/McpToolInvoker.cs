using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>呼叫端（SDK）提供的不可信資訊：client 名稱與版本、<c>X-Formwright-Job</c> 標頭；只進稽核摘要。</summary>
public sealed record McpCallContext(string? ClientName = null, string? ClientVersion = null, string? Job = null);

/// <summary>
/// 工具執行器（mcp1a 計畫判斷 17、19）：通道 → 找工具 → 範圍 → 速率與名額保留（判斷 13）→ 執行 → 格式化與大小判斷（判斷 14）→ 稽核 → 釋放保留。
/// 回傳格式化好的結果，SDK 介面只負責包成 <c>CallToolResult</c>。不認得的工具回 <c>unknown_tool</c> 錯誤結果（模型可修正），不用 JSON-RPC error。
/// <para>
/// 每次呼叫恰好一列稽核（判斷 17 的結束路徑表）：成功 Success；不存在的工具 Invalid（<c>mcp.unknownTool</c>）；範圍、速率、handler 的 Forbidden 是 Denied；
/// 參數錯誤與 handler 的 Invalid、NotFound、Conflict 是 Invalid；放不下、例外、取消是 Failed。稽核在格式化之後、回應之前以
/// <see cref="IAuditLog.WriteNowAsync"/> 寫，寫不進去就不回資料、改回 <c>audit_unavailable</c>（T14）；名額保留在稽核寫完之後才釋放。
/// </para>
/// </summary>
public sealed class McpToolInvoker(
    IEnumerable<IMcpTool> tools, IRequestChannel channel, McpPolicies policies, McpRateGate rate, McpUsage usage, IAuditLog audit,
    IMessageCatalog messages, RequestLocale locale, TimeProvider time, ILogger<McpToolInvoker> log)
{
    private readonly IReadOnlyList<IMcpTool> _tools = tools.ToList();

    /// <summary><c>tools/list</c>：權杖範圍涵蓋的工具（範圍是 null 的 <c>whoami</c> 一律列），依名稱 Ordinal 排序。</summary>
    public IReadOnlyList<IMcpTool> ListFor(IReadOnlyList<string> scopes) =>
        _tools.Where(t => t.Descriptor.Scope is null || scopes.Contains(t.Descriptor.Scope, StringComparer.Ordinal))
            .OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal).ToList();

    public async Task<McpFormattedResult> InvokeAsync(string name, JsonElement? arguments, McpCallContext context, CancellationToken ct)
    {
        // 通道不是 MCP 是程式錯誤（只有 /mcp 閘門換上的權杖身分會進來），fail closed；這時沒有可信的權杖身分，不寫稽核
        if (channel.Channel != RequestChannel.Mcp) throw new InvalidOperationException("工具執行器只能在 MCP 通道使用");
        var started = time.GetTimestamp();
        var lc = locale.Current;
        string Text(MessageRef m) => m.Resolve(messages, lc);

        var args = arguments is { ValueKind: not JsonValueKind.Undefined } a ? a : EmptyObject;
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Descriptor.Name, name, StringComparison.Ordinal));
        McpReservation? reservation = null;
        McpToolOutcome? outcome = null;
        McpFormattedResult result;
        AuditResult auditResult;
        string? reason;
        try
        {
            try
            {
                (result, auditResult, reason) = await RunAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                (result, auditResult, reason) = (McpResultFormatter.Error(McpErrorCodes.InternalError, Text(McpMessages.InternalError)),
                    AuditResult.Failed, "cancelled");
            }
            catch (Exception ex)
            {
                // 訊息不帶例外內容
                log.LogError(ex, "MCP 工具 {Tool} 執行失敗", name);
                (result, auditResult, reason) = (McpResultFormatter.Error(McpErrorCodes.InternalError, Text(McpMessages.InternalError)),
                    AuditResult.Failed, "exception");
            }

            try
            {
                // 稽核收尾與請求取消脫鉤：工具取消後照常回傳、或取消發生在稽核提交途中，都要恰好寫一列（名額才不會白白釋放）
                await audit.WriteNowAsync(Entry(tool, name, args, outcome, auditResult, reason, context, started), CancellationToken.None);
            }
            catch (AuditWriteException ex)
            {
                log.LogError(ex.InnerException, "MCP 工具 {Tool} 的稽核寫不進去，不回傳資料", name);
                result = McpResultFormatter.Error(McpErrorCodes.AuditUnavailable, Text(McpMessages.AuditUnavailable));
            }
        }
        finally
        {
            reservation?.Release();
        }
        ct.ThrowIfCancellationRequested();
        return result;

        async Task<(McpFormattedResult, AuditResult, string?)> RunAsync()
        {
            if (tool is null)
                return (McpResultFormatter.Error(McpErrorCodes.UnknownTool, Text(McpMessages.UnknownTool)), AuditResult.Invalid, McpErrorCodes.UnknownTool);
            if (tool.Descriptor.Scope is { } scope && !channel.Scopes.Contains(scope, StringComparer.Ordinal))
                return (McpResultFormatter.Error(McpErrorCodes.ScopeDenied, Text(McpMessages.ScopeDenied)), AuditResult.Denied, McpErrorCodes.ScopeDenied);

            var snapshot = await policies.ReadAsync(ct);
            var tokenId = channel.TokenId ?? throw new InvalidOperationException("MCP 通道沒有權杖 id");
            var decision = await rate.TryReserveAsync(tokenId, snapshot.CallsPerMinute, snapshot.CallsPerDay, c => usage.CountTodayAsync(tokenId, c), ct);
            if (!decision.Allowed)
            {
                var limited = decision.Denial == McpRateDenial.Minute
                    ? McpResultFormatter.Error(McpErrorCodes.RateLimited, Text(McpMessages.RateLimitedMinute(snapshot.CallsPerMinute)),
                        retryAfterSeconds: decision.RetryAfterSeconds)
                    : McpResultFormatter.Error(McpErrorCodes.RateLimited, Text(McpMessages.RateLimitedDay(snapshot.CallsPerDay)),
                        retryAfterSeconds: SecondsUntil(usage.NextResetUtc));
                return (limited, AuditResult.Denied, McpErrorCodes.RateLimited);
            }
            reservation = decision.Reservation;

            outcome = Mapped(await tool.RunAsync(new McpToolCall(args, snapshot, lc.Locale), ct));
            if (outcome.IsError)
            {
                var code = outcome.ErrorCode ?? McpErrorCodes.InternalError;
                var kind = code switch
                {
                    McpErrorCodes.Forbidden or McpErrorCodes.RateLimited or McpErrorCodes.ScopeDenied => AuditResult.Denied,
                    McpErrorCodes.InternalError => AuditResult.Failed,
                    _ => AuditResult.Invalid,
                };
                return (Format(tool.Descriptor.Name, outcome, Text), kind, code);
            }
            var fitted = McpResultFormatter.Fit(tool.Descriptor.Name, outcome, Text(McpMessages.ResultTooLarge));
            return fitted.IsError ? (fitted, AuditResult.Failed, McpErrorCodes.ResultTooLarge) : (fitted, AuditResult.Success, null);
        }
    }

    private AuditEntry Entry(IMcpTool? tool, string name, JsonElement args, McpToolOutcome? outcome, AuditResult result, string? reason,
        McpCallContext context, long started)
    {
        var summary = new AuditSummary().Add("args", McpAuditSummary.Args(args));
        if (outcome?.RecordIds is { Count: > 0 } ids) summary.Add("ids", ids.Take(McpLimits.AuditRecordIdsMax).ToList());
        if (context.ClientName is not null || context.ClientVersion is not null)
            summary.Add("client", new { name = McpAuditSummary.Cut(context.ClientName), version = McpAuditSummary.Cut(context.ClientVersion) });
        summary.Add("ms", (long)time.GetElapsedTime(started).TotalMilliseconds);
        if (context.Job is not null) summary.Add("job", McpAuditSummary.Cut(context.Job));
        return new AuditEntry(tool?.Descriptor.AuditCode ?? McpToolCatalog.UnknownToolAuditCode, result,
            TargetKind: outcome?.TargetKind, TargetId: outcome?.TargetId, TargetLabel: name, ReasonCode: reason, Summary: summary.ToJson(),
            ArgumentsHash: McpArguments.Hash(args), ResultCount: outcome is { IsError: false } ? outcome.ResultCount : null);
    }

    private int SecondsUntil(DateTime utc) => Math.Max(1, (int)Math.Ceiling((utc - time.GetUtcNow().UtcDateTime).TotalSeconds));

    /// <summary>錯誤結果超過上限時，訊息截到這個長度（技術限制，遠小於結果上限）。</summary>
    private const int ErrorMessageMax = 1000;

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
        var code = outcome.ErrorCode ?? McpErrorCodes.InternalError;
        var error = McpResultFormatter.Error(code, message, fields, outcome.RetryAfterSeconds);
        // 錯誤結果也受單次結果上限（判斷 14）：放不下就拿掉欄位明細、訊息截短，代碼不變
        return error.Size <= McpLimits.MaxResultChars ? error
            : McpResultFormatter.Error(code, message.Length > ErrorMessageMax ? message[..ErrorMessageMax] : message,
                retryAfterSeconds: outcome.RetryAfterSeconds);
    }
}
