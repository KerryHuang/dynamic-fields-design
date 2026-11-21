using DynamicModules.Application.Dynamic.McpService.Tokens;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>AI 連線的個人存取權杖（MCP 設計 §13.4、mcp1a 計畫判斷 7）：本人 me/mcp/*，管理員 system/mcp/*。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class McpTokensController(IMediator mediator, ClientIpResolver clientIp, IConfiguration config) : ControllerBase
{
    public record CreateTokenBody(string? Name, List<string>? Scopes, DateOnly? ExpiresOn, string? Password);

    /// <summary>伺服器網址沿用 <c>Ops:PublicUrl</c>（判斷 15）；沒設時用這個請求的 scheme＋host。</summary>
    [HttpGet("me/mcp")]
    public async Task<IActionResult> Info()
    {
        var publicUrl = config["Ops:PublicUrl"];
        var baseUrl = string.IsNullOrWhiteSpace(publicUrl) ? $"{Request.Scheme}://{Request.Host}" : publicUrl;
        return (await mediator.Send(new GetMyMcpInfoRequest(baseUrl))).ToActionResult(this);
    }

    [HttpGet("me/mcp/tokens")]
    public async Task<IActionResult> List() => (await mediator.Send(new ListMyMcpTokensRequest())).ToActionResult(this);

    [HttpPost("me/mcp/tokens")]
    public async Task<IActionResult> Create(CreateTokenBody body) =>
        (await mediator.Send(new CreateMcpTokenRequest(body.Name, body.Scopes, body.ExpiresOn, body.Password, clientIp.Resolve(HttpContext))))
        .ToActionResult(this);

    [HttpPost("me/mcp/tokens/{id:long}/revoke")]
    public async Task<IActionResult> Revoke(long id) => (await mediator.Send(new RevokeMyMcpTokenRequest(id))).ToActionResult(this);

    [HttpGet("me/mcp/tokens/{id:long}/calls")]
    public async Task<IActionResult> Calls(long id) => (await mediator.Send(new GetMyMcpTokenCallsRequest(id))).ToActionResult(this);

    [HttpGet("system/mcp/tokens")]
    public async Task<IActionResult> AdminList([FromQuery] string? userId, [FromQuery] string? status) =>
        (await mediator.Send(new AdminListMcpTokensRequest(userId, status))).ToActionResult(this);

    [HttpPost("system/mcp/tokens/{id:long}/revoke")]
    public async Task<IActionResult> AdminRevoke(long id) => (await mediator.Send(new AdminRevokeMcpTokenRequest(id))).ToActionResult(this);

    [HttpPost("system/mcp/users/{userId}/revoke-all")]
    public async Task<IActionResult> AdminRevokeAll(string userId) =>
        (await mediator.Send(new AdminRevokeAllMcpTokensRequest(userId))).ToActionResult(this);
}
