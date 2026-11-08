using DynamicModules.Application.Dynamic.LedgerService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>分錄設定的設計器端點（設計 §11.2）。沖銷的執行期端點在 Records 之下。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class LedgerController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/ledger")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetLedgerRequest(key))).ToActionResult(this);

    [HttpPut("modules/{key}/ledger")]
    public async Task<IActionResult> Save(string key, LedgerInput body) =>
        (await mediator.Send(new SaveLedgerRequest(key, body))).ToActionResult(this);

    [HttpDelete("modules/{key}/ledger")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteLedgerRequest(key))).ToActionResult(this);
}
