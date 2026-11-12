using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>只給 OpsTestApp 用的測試端點（落在 /api/v3/dynamic 底下，受 SessionMiddleware 保護）。</summary>
[ApiController]
[Route("api/v3/dynamic/test-ops")]
public class OpsTestController(ILogger<OpsTestController> logger) : ControllerBase
{
    [HttpGet("slow")]
    public async Task<IActionResult> Slow([FromQuery] int ms)
    {
        await Task.Delay(ms);
        return Ok();
    }

    [HttpGet("modules/{key}/log")]
    public IActionResult LogOne(string key)
    {
        logger.LogInformation("handler log {Key}", key);
        return Ok();
    }

    [HttpGet("bad-request")]
    public IActionResult Bad() => throw new Microsoft.AspNetCore.Http.BadHttpRequestException("x", 413);
}
