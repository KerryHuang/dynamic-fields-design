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

    /// <summary>丟一個未處理例外，訊息由呼叫端給（o2b 事件測試）。</summary>
    [HttpGet("throw")]
    public IActionResult Throw([FromQuery] string message) => throw new InvalidOperationException(message);

    /// <summary>非資料庫的外部連線主機名稱解析失敗（裸 SocketException，不能被當成資料庫連不上）。</summary>
    [HttpGet("throw-dns")]
    public IActionResult ThrowDns() => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);

    [HttpGet("bad-request")]
    public IActionResult Bad() => throw new Microsoft.AspNetCore.Http.BadHttpRequestException("x", 413);
}
