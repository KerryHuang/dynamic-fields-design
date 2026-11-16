using System.Text.Json;
using DynamicModules.Application.Dynamic.PolicyService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>政策設定（設計 docs/superpowers/specs/2026-11-11-policy-settings-design.md §7）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class PoliciesController(IMediator mediator) : ControllerBase
{
    /// <summary>PUT body；<c>{}</c> 與 <c>{"value":null}</c> 都是回到上一層。</summary>
    public record PolicyValueBody(JsonElement? Value);

    [HttpGet("system/policies")]
    public async Task<IActionResult> ListSystem() =>
        (await mediator.Send(new GetSystemPoliciesRequest())).ToActionResult(this);

    [HttpPut("system/policies/{code}")]
    public async Task<IActionResult> UpdateSystem(string code, PolicyValueBody body) =>
        (await mediator.Send(new UpdateSystemPolicyRequest(code, body.Value))).ToActionResult(this);

    [HttpGet("modules/{key}/policies")]
    public async Task<IActionResult> ListModule(string key) =>
        (await mediator.Send(new GetModulePoliciesRequest(key))).ToActionResult(this);

    [HttpPut("modules/{key}/policies/{code}")]
    public async Task<IActionResult> UpdateModule(string key, string code, PolicyValueBody body) =>
        (await mediator.Send(new UpdateModulePolicyRequest(key, code, body.Value))).ToActionResult(this);

    [HttpGet("policies/effective")]
    public async Task<IActionResult> Effective([FromQuery(Name = "module")] string? module) =>
        (await mediator.Send(new GetEffectivePoliciesRequest(module))).ToActionResult(this);
}
