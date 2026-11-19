using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Audit;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using System.Text.Json;
using System.Security.Claims;
using DynamicModules.WebAPI.Ops;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>維運資訊（設計 docs/superpowers/specs/2026-11-11-ops-observability-design.md §10）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class OpsController(IMediator mediator) : ControllerBase
{
    [HttpGet("system/ops/info")]
    public async Task<IActionResult> Info() => (await mediator.Send(new GetOpsInfoRequest())).ToActionResult(this);

    /// <summary>稽核清單（維運設計 §17.5）；時間 ISO 8601 UTC，cursor 是上一頁回的 nextCursor。</summary>
    [HttpGet("system/ops/audit")]
    public async Task<IActionResult> Audit(
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] string? actorUserId, [FromQuery] string? action,
        [FromQuery] string? actionGroup, [FromQuery] string? channel, [FromQuery] string? result, [FromQuery] string? targetKind,
        [FromQuery] string? targetId, [FromQuery] string? cursor, [FromQuery] int? limit) =>
        (await mediator.Send(new ListAuditRequest(fromUtc, toUtc, actorUserId, action, actionGroup, channel, result, targetKind, targetId,
            cursor, limit))).ToActionResult(this);

    [HttpGet("system/ops/audit/actions")]
    public async Task<IActionResult> AuditActions() => (await mediator.Send(new GetAuditActionsRequest())).ToActionResult(this);

    [HttpGet("system/ops/audit/{id:long}")]
    public async Task<IActionResult> AuditDetail(long id) => (await mediator.Send(new GetAuditRequest(id))).ToActionResult(this);

    /// <summary>監控總覽（維運設計 §17.5）：整體狀態、各檢查、未結事件數、需要處理清單、排程器、告警通道（o2c 前一律未設定）。</summary>
    [HttpGet("system/ops/overview")]
    public async Task<IActionResult> Overview() => (await mediator.Send(new GetOpsOverviewRequest())).ToActionResult(this);

    [HttpGet("system/ops/health-checks")]
    public async Task<IActionResult> HealthChecks() => (await mediator.Send(new ListHealthChecksRequest())).ToActionResult(this);

    /// <summary>改開關、間隔、門檻：欄位省略＝不變，送 null＝回到出廠值。</summary>
    [HttpPut("system/ops/health-checks/{code}")]
    public async Task<IActionResult> UpdateHealthCheck(string code, [FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return BadRequest();
        return (await mediator.Send(new UpdateHealthCheckRequest(code,
            Field(body, "enabled", e => e.GetBoolean()), Field(body, "intervalMinutes", e => e.GetInt32()),
            Field(body, "warn", e => e.GetInt64()), Field(body, "critical", e => e.GetInt64())))).ToActionResult(this);
    }

    [HttpPost("system/ops/health-checks/{code}/run")]
    public async Task<IActionResult> RunHealthCheck(string code) => (await mediator.Send(new RunHealthCheckRequest(code))).ToActionResult(this);

    [HttpGet("system/ops/health-checks/{code}/results")]
    public async Task<IActionResult> HealthCheckResults(string code, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] int? limit) =>
        (await mediator.Send(new GetHealthCheckResultsRequest(code, fromUtc, toUtc, limit))).ToActionResult(this);

    /// <summary>事件清單；status 可逗號分隔多個（open,acknowledged），cursor 是上一頁回的 nextCursor。</summary>
    [HttpGet("system/ops/incidents")]
    public async Task<IActionResult> Incidents([FromQuery] string? status, [FromQuery] string? category, [FromQuery] string? severity,
        [FromQuery] string? cursor, [FromQuery] int? limit) =>
        (await mediator.Send(new ListIncidentsRequest(status, category, severity, cursor, limit))).ToActionResult(this);

    [HttpGet("system/ops/incidents/{id:long}")]
    public async Task<IActionResult> Incident(long id) => (await mediator.Send(new GetIncidentRequest(id))).ToActionResult(this);

    [HttpPost("system/ops/incidents/{id:long}/acknowledge")]
    public async Task<IActionResult> Acknowledge(long id, IncidentStateBody body) =>
        (await mediator.Send(new AcknowledgeIncidentRequest(id, body.ExpectedStatus))).ToActionResult(this);

    [HttpPost("system/ops/incidents/{id:long}/resolve")]
    public async Task<IActionResult> Resolve(long id, IncidentStateBody body) =>
        (await mediator.Send(new ResolveIncidentRequest(id, body.ExpectedStatus, body.Note))).ToActionResult(this);

    public sealed record IncidentStateBody(string? ExpectedStatus, string? Note);

    /// <summary>JSON 欄位的三態：沒有這個鍵＝不變；值是 null＝回出廠值；型別不對當成不合法的值（交給 400）。</summary>
    private static Optional<T> Field<T>(JsonElement body, string name, Func<JsonElement, T> read) where T : struct
    {
        if (!body.TryGetProperty(name, out var e)) return Optional<T>.Unset;
        if (e.ValueKind == JsonValueKind.Null) return new(true, null);
        try
        {
            return new(true, read(e));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new BadHttpRequestException($"欄位 {name} 的型別不對");
        }
    }

    /// <summary>前端回報程式錯誤：登入與否都收（SessionMiddleware 的可選登入分支）；只記 log，回 204。</summary>
    [HttpPost("ops/client-errors")]
    [TypeFilter(typeof(ClientErrorGuardFilter))]
    public async Task<IActionResult> ClientErrors(ClientErrorReport report)
    {
        var result = await mediator.Send(new ReportClientErrorRequest(report, User.FindFirstValue(ClaimTypes.NameIdentifier)));
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }
}
