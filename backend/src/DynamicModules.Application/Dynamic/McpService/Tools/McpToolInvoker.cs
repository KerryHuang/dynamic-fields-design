using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.WriteLoop;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>呼叫端（SDK）提供的不可信資訊：client 名稱與版本、<c>X-Formwright-Job</c> 標頭；只進稽核摘要。</summary>
/// <summary>確認已提交、但結果沒有回傳時的原因（<c>meta.dataOmitted</c>）。</summary>
public static class McpDataOmitted
{
    public const string ResultTooLarge = "result_too_large", FormatFailed = "format_failed", PostCommitError = "post_commit_error";
}

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
    IMessageCatalog messages, RequestLocale locale, ISender sender, IMcpConfirmationScope confirmations, IMcpWriteScope writes,
    IOperationContext operation, ICurrentUser user, IServiceScopeFactory scopes, TimeProvider time, ILogger<McpToolInvoker> log)
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
        // 本次呼叫的序號（判斷 18）：只認這次的「確認已提交」，前一次呼叫的提交不會讓這次漏寫稽核
        var call = confirmations.BeginCall();
        writes.Disarm();
        // 冪等鍵只認這次工具參數的 idempotencyKey（mcp2a 判斷 5）：前一次呼叫的、或 HTTP 標頭帶進來的都清掉
        (operation.ClientKey, operation.Replayed) = (null, false);
        // 寫入工具的確認或寫入稽核已在交易內寫了這次的那一列（判斷 3、18）
        bool Committed() => confirmations.IsCommitted(call) || writes.IsCommitted(call);
        var started = time.GetTimestamp();
        var lc = locale.Current;
        string Text(MessageRef m) => m.Resolve(messages, lc);

        var args = arguments is { ValueKind: not JsonValueKind.Undefined } a ? a : EmptyObject;
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Descriptor.Name, name, StringComparison.Ordinal));
        // 高風險工具的確認碼不進參數雜湊、稽核摘要與工具參數（預演與執行的雜湊才會相同，明文也不落地）
        string? confirmationCode = null;
        if (tool?.Descriptor.Risk == McpRisk.HighRisk) (args, confirmationCode) = SplitConfirmation(args);
        McpReservation? reservation = null;
        McpToolOutcome? outcome = null;
        McpFormattedResult result;
        AuditResult auditResult;
        string? reason;
        long? confirmationId = null;
        AuditConfirmationOutcome? confirmationOutcome = null;
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
            catch (Exception ex) when (Committed())
            {
                // 確認或寫入已提交之後才丟例外（提交後的收尾或工具的後處理）：動作已生效，不回錯誤免得模型重試（判斷 18）
                log.LogError(ex, "MCP 工具 {Tool} 的確認已提交，之後的處理失敗", name);
                (result, auditResult, reason) = (Executed(name, McpDataOmitted.PostCommitError), AuditResult.Success, null);
            }
            catch (Exception ex)
            {
                // 訊息不帶例外內容
                log.LogError(ex, "MCP 工具 {Tool} 執行失敗", name);
                (result, auditResult, reason) = (McpResultFormatter.Error(McpErrorCodes.InternalError, Text(McpMessages.InternalError)),
                    AuditResult.Failed, "exception");
            }

            // 確認已提交：behavior 在交易內寫的那一列就是這次呼叫唯一的一列（判斷 17），不再寫
            if (!Committed())
                try
                {
                    // 稽核收尾與請求取消脫鉤：工具取消後照常回傳、或取消發生在稽核提交途中，都要恰好寫一列（名額才不會白白釋放）
                    await audit.WriteNowAsync(Entry(tool, name, args, outcome, auditResult, reason, context, started, confirmationId,
                        confirmationOutcome), CancellationToken.None);
                }
                catch (AuditWriteException ex)
                {
                    log.LogError(ex.InnerException, "MCP 工具 {Tool} 的稽核寫不進去，不回傳資料", name);
                    result = McpResultFormatter.Error(McpErrorCodes.AuditUnavailable, Text(McpMessages.AuditUnavailable));
                }
        }
        finally
        {
            writes.Disarm();
            // 不留給同一個 scope 的下一次呼叫；/mcp 的回應也不因此帶 Idempotent-Replay 標頭（回放改由 meta.replayed 表達）
            (operation.ClientKey, operation.Replayed) = (null, false);
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

            if (tool.Descriptor.Risk == McpRisk.Write)
            {
                // 執行器自己的參數驗證（判斷 3）：只有冪等鍵與迴圈目標；不過就不上膛、不呼叫工具
                if (WriteArgErrors(tool.Descriptor, args) is { Count: > 0 } errors)
                {
                    outcome = McpToolOutcome.InvalidArguments(errors);
                    return (Format(tool.Descriptor.Name, outcome, Text), AuditResult.Invalid, McpErrorCodes.InvalidArguments);
                }
                var key = args.GetProperty(McpArgs.IdempotencyKey).GetString()!;
                // 寫入迴圈（判斷 8）：在上膛之前；擋下時工具不執行，稽核只有執行器事後寫的那一列 Denied
                if (tool.Descriptor.LoopTargetArg is { } loopArg && args.TryGetProperty(loopArg, out var loopTarget)
                    && loopTarget.ValueKind == JsonValueKind.Number && await WriteLoopAsync(tokenId, loopTarget.GetInt64(), key) is { } blocked)
                {
                    outcome = blocked;
                    return (Format(tool.Descriptor.Name, outcome, Text), AuditResult.Denied, McpErrorCodes.WriteLoop);
                }
                // IdempotencyBehavior 接手（判斷 5）；設在上膛之前，摘要的 op 與事件事實的 OperationId 都是這把 key
                operation.ClientKey = key;
                writes.Arm(call, new McpWriteAudit(tool.Descriptor.AuditCode, name, McpArguments.Hash(args),
                    Summary(args, null, context, started: null, op: operation.OperationId)));
            }

            var toolCall = new McpToolCall(args, snapshot, lc.Locale);
            outcome = Mapped(tool.Descriptor.Risk != McpRisk.HighRisk ? await tool.RunAsync(toolCall, ct)
                : confirmationCode is null ? await PreviewAsync(tool, toolCall, tokenId)
                : await tool.ExecuteAsync(toolCall, new McpConfirmationTicket(confirmationCode, tokenId, name, McpArguments.Hash(args),
                    tool.Descriptor.AuditCode, Summary(args, null, context, started: null, strict: true)), ct));
            if (tool.Descriptor.Risk == McpRisk.Write && !outcome.IsError)
                outcome = outcome with { Meta = WithReplayed(outcome.Meta, operation.Replayed) };

            // 確認或寫入已提交（判斷 18「確認提交之後」、mcp2a 判斷 3）：格式化成功而且放得下照常回，否則回非錯誤的「已執行、結果省略」
            if (Committed()) return (CommittedResult(tool.Descriptor.Name, outcome), AuditResult.Success, null);

            if (outcome.IsError)
            {
                var code = outcome.ErrorCode ?? McpErrorCodes.InternalError;
                if (code == McpErrorCodes.ConfirmationInvalid) confirmationOutcome = AuditConfirmationOutcome.Mismatch;
                var kind = code switch
                {
                    McpErrorCodes.Forbidden or McpErrorCodes.RateLimited or McpErrorCodes.ScopeDenied => AuditResult.Denied,
                    McpErrorCodes.InternalError => AuditResult.Failed,
                    _ => AuditResult.Invalid,
                };
                return (Format(tool.Descriptor.Name, outcome, Text), kind, code);
            }
            var fitted = McpResultFormatter.Fit(tool.Descriptor.Name, outcome, Text(McpMessages.ResultTooLarge));
            return fitted.IsError ? (fitted, AuditResult.Failed, McpErrorCodes.ResultTooLarge) : (fitted, AuditResult.Success, AuditReasonOf(outcome));
        }

        // 寫入迴圈（判斷 8、D4）：這把 key 已有冪等紀錄（重送）不擋，讓它照常回放；只計成功、非回放、非預覽的寫入。
        // 達門檻回 write_loop（目標是那筆記錄），並另開 scope 開或更新維運事件；事件寫不進去只記 Error log，不影響回給模型的錯誤
        async Task<McpToolOutcome?> WriteLoopAsync(long tokenId, long recordId, string key)
        {
            if (await usage.HasOperationAsync(user.UserId, key, ct)) return null;
            var (triggers, minutes) = await policies.ReadLoopAsync(ct);
            var count = await usage.CountLoopWritesAsync(tokenId, recordId, minutes, ct);
            if (count < triggers) return null;
            await RecordLoopIncidentAsync(new RecordMcpWriteLoopIncidentRequest(tokenId, recordId, count, minutes, time.GetUtcNow().UtcDateTime));
            return McpToolOutcome.Error(McpErrorCodes.WriteLoop, McpMessages.WriteLoop(count, minutes))
                with { TargetKind = McpWriteTargets.Record, TargetId = recordId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        // 工具沒回錯誤、本次也沒提交時才採用；值限定常數集合（判斷 3）
        string? AuditReasonOf(McpToolOutcome done)
        {
            if (done.AuditReason is not { } r) return null;
            if (McpAuditReasons.All.Contains(r)) return r;
            log.LogError("MCP 工具 {Tool} 回了未登記的稽核原因 {Reason}，不寫進稽核", name, r);
            return null;
        }

        // 預演（判斷 18）：工具給摘要與目標版本 → 建確認碼（交易型請求）→ 回非錯誤結果，碼在 meta；稽核記 Preview 與確認列 id
        async Task<McpToolOutcome> PreviewAsync(IMcpTool highRisk, McpToolCall toolCall, long tokenId)
        {
            var preview = await highRisk.PreviewAsync(toolCall, ct);
            if (preview.Failure is { } failed) return failed;
            var summary = JsonSerializer.Serialize(preview.Summary, McpResultFormatter.Json);
            var created = await sender.Send(new CreateMcpConfirmationRequest(name, McpArguments.Hash(args), preview.TargetVersion, summary), ct);
            if (!created.IsSuccess) return McpToolOutcome.From(created);
            var c = (McpConfirmationCreated)created.Data!;
            (confirmationId, confirmationOutcome) = (c.Id, AuditConfirmationOutcome.Preview);
            return McpToolOutcome.Ok(new { preview = preview.Summary },
                    new { confirmation = new { code = c.Code, expiresAt = c.ExpiresUtc }, instruction = McpTexts.ConfirmationInstruction })
                with { TargetKind = preview.TargetKind, TargetId = preview.TargetId };
        }

        McpFormattedResult CommittedResult(string tool, McpToolOutcome done)
        {
            try
            {
                var fitted = McpResultFormatter.Fit(tool, done, Text(McpMessages.ResultTooLarge));
                return fitted.IsError ? Executed(tool, McpDataOmitted.ResultTooLarge) : fitted;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MCP 工具 {Tool} 的確認已提交，結果格式化失敗", tool);
                return Executed(tool, McpDataOmitted.FormatFailed);
            }
        }

        McpFormattedResult Executed(string tool, string omitted) =>
            McpResultFormatter.Executed(tool, confirmations.ConfirmationId, omitted, McpTexts.ConfirmedResultOmitted);
    }

    /// <summary>迴圈事件另開 scope 送（照 <c>RuleFailureBehavior</c>）：執行器在交易外，事件只能在交易型請求裡寫。</summary>
    private async Task RecordLoopIncidentAsync(RecordMcpWriteLoopIncidentRequest request)
    {
        try
        {
            using var system = ActingScope.Begin("");
            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, CancellationToken.None) is { IsSuccess: false } failed)
                log.LogError("AI 寫入迴圈事件寫入失敗：{Result}", failed.Message ?? failed.Code);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AI 寫入迴圈事件寫入失敗");
        }
    }

    /// <summary>從高風險工具的參數拿掉 <c>confirmationCode</c>；不是字串的碼當成空碼（一定不符）。</summary>
    private static (JsonElement Args, string? Code) SplitConfirmation(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(McpConfirmationArgs.Code, out var code)) return (args, null);
        var rest = new JsonObject();
        foreach (var p in args.EnumerateObject())
            if (p.Name != McpConfirmationArgs.Code) rest[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return (JsonDocument.Parse(rest.ToJsonString()).RootElement.Clone(), code.ValueKind == JsonValueKind.String ? code.GetString() ?? "" : "");
    }

    private AuditEntry Entry(IMcpTool? tool, string name, JsonElement args, McpToolOutcome? outcome, AuditResult result, string? reason,
        McpCallContext context, long started, long? confirmationId, AuditConfirmationOutcome? confirmationOutcome) =>
        new(tool?.Descriptor.AuditCode ?? McpToolCatalog.UnknownToolAuditCode, result,
            TargetKind: outcome?.TargetKind, TargetId: outcome?.TargetId, TargetLabel: name, ReasonCode: reason,
            Summary: Summary(args, outcome?.RecordIds, context, started, strict: tool is null || tool.Descriptor.Risk != McpRisk.Read),
            ArgumentsHash: McpArguments.Hash(args), ResultCount: outcome is { IsError: false } ? outcome.ResultCount : null,
            ConfirmationId: confirmationId, ConfirmationOutcome: confirmationOutcome);

    /// <summary>
    /// 稽核摘要（判斷 17）：參數摘要、前 20 個記錄 id、client、耗時（確認與寫入稽核在交易內寫，不帶耗時）、任務標籤、
    /// operation id（寫入稽核，mcp2a 判斷 3）。寫入、高風險、不存在的工具用 strict 摘要（mcp2a 判斷 15）。
    /// </summary>
    private string? Summary(JsonElement args, IReadOnlyList<long>? recordIds, McpCallContext context, long? started, bool strict = false,
        string? op = null)
    {
        var summary = new AuditSummary().Add("args", McpAuditSummary.Args(args, strict || op is not null));
        if (op is not null) summary.Add("op", op);
        if (recordIds is { Count: > 0 } ids) summary.Add("ids", ids.Take(McpLimits.AuditRecordIdsMax).ToList());
        if (context.ClientName is not null || context.ClientVersion is not null)
            summary.Add("client", new { name = McpAuditSummary.Cut(context.ClientName), version = McpAuditSummary.Cut(context.ClientVersion) });
        if (started is { } t) summary.Add("ms", (long)time.GetElapsedTime(t).TotalMilliseconds);
        if (context.Job is not null) summary.Add("job", McpAuditSummary.Cut(context.Job));
        return summary.ToJson();
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
        if (r.IsConflict && r.Code == "stale")
            return outcome with { ErrorCode = McpErrorCodes.Stale, ErrorMessage = MessageRef.Of(MessageText.Stale) };
        // 其他衝突（檢查失敗、子任務未完成、已在簽核中等）：訊息照 handler，欄位帶機器碼與參數，模型能據此修正重試（mcp2a 判斷 4）
        if (r.IsConflict) return outcome with { ErrorCode = McpErrorCodes.Conflict, Fields = ConflictFields(r) };
        if (r.IsUnprocessable && r.MessageKey == MessageText.IdempotencyKeyReused)
            return outcome with { ErrorCode = McpErrorCodes.IdempotencyKeyReused };
        // 確認碼不符一律同一句、不帶欄位（不說明是哪一項，判斷 18）
        if (r.Code == McpErrorCodes.ConfirmationInvalid)
            return outcome with
            {
                ErrorCode = McpErrorCodes.ConfirmationInvalid, ErrorMessage = McpMessages.ConfirmationInvalid,
                Fields = new Dictionary<string, MessageRef>(),
            };
        // 其餘（Invalid、Unprocessable、TooManyRequests）照 handler 的訊息
        return outcome with { ErrorCode = r.IsTooManyRequests ? McpErrorCodes.RateLimited : McpErrorCodes.Invalid };
    }

    /// <summary>衝突的機器碼與參數（<c>code</c>、<c>params.&lt;名稱&gt;</c>）；都沒有時是 null。</summary>
    private static Dictionary<string, MessageRef>? ConflictFields(ServiceResult r)
    {
        var fields = new Dictionary<string, MessageRef>(StringComparer.Ordinal);
        if (r.Code is { } code) fields["code"] = MessageRef.Literal(code);
        if (r.Params is { } p && JsonSerializer.SerializeToElement(p, p.GetType(), McpResultFormatter.Json) is { ValueKind: JsonValueKind.Object } o)
            foreach (var prop in o.EnumerateObject())
                fields["params." + prop.Name] = MessageRef.Literal(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.GetRawText());
        return fields.Count > 0 ? fields : null;
    }

    /// <summary>寫入工具的結果在 meta 加 <c>replayed</c>（這次是不是回放先前成功的結果，判斷 5）。</summary>
    private static JsonObject WithReplayed(object? meta, bool replayed)
    {
        var node = meta is null ? null : JsonSerializer.SerializeToNode(meta, meta.GetType(), McpResultFormatter.Json);
        var o = node as JsonObject ?? new JsonObject();
        o["replayed"] = replayed;
        return o;
    }

    /// <summary>寫入工具上膛前的參數驗證：<c>idempotencyKey</c> 必填且格式對、迴圈目標是正整數（其他參數由工具自己驗）。</summary>
    private static Dictionary<string, MessageRef> WriteArgErrors(McpToolDescriptor tool, JsonElement args)
    {
        var errors = new Dictionary<string, MessageRef>(StringComparer.Ordinal);
        if (args.ValueKind != JsonValueKind.Object)
        {
            errors[McpArgs.ArgumentsKey] = McpMessages.InvalidArgument(McpArgs.ArgumentsKey);
            return errors;
        }
        // 必填（判斷 5）：AI client 逾時重送是常態，選填的話模型多半不帶，重送就重複寫入；沒帶與格式不符同一句
        if (!args.TryGetProperty(McpArgs.IdempotencyKey, out var key) || key.ValueKind != JsonValueKind.String
            || !McpArgs.IsIdempotencyKey(key.GetString()))
            errors[McpArgs.IdempotencyKey] = McpMessages.IdempotencyKeyRequired;
        if (tool.LoopTargetArg is { } target && args.TryGetProperty(target, out var t) && t.ValueKind != JsonValueKind.Null
            && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var id) || id < 1))
            errors[target] = McpMessages.InvalidArgument(target);
        return errors;
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
