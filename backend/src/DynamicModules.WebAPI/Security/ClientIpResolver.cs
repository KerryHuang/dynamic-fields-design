namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 限流用的來源 IP。站台在反向代理後面時，連線來源是代理，所有人會共用一個額度；
/// 設了 Auth:ClientIpHeader（代理自己加、不接受客戶端帶入的那個標頭）才改讀它，格式不對就退回連線來源。
/// 沒設定時一律忽略所有標頭，偽造的值換不到新額度。
/// </summary>
public sealed class ClientIpResolver(IConfiguration config)
{
    public string? Resolve(HttpContext http)
    {
        var header = config["Auth:ClientIpHeader"];
        if (!string.IsNullOrWhiteSpace(header) && http.Request.Headers.TryGetValue(header, out var values)
            && System.Net.IPAddress.TryParse(values.ToString().Trim(), out var fromHeader))
            return fromHeader.ToString();
        return http.Connection.RemoteIpAddress?.ToString();
    }
}
