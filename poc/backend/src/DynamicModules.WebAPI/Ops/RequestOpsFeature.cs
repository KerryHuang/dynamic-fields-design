namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 請求開始時就取好的路由樣板與模組代碼。ExceptionHandlerMiddleware 會清掉 endpoint 與 route values，
/// 所以結束行與例外 log 都讀這份，不再讀 <c>GetEndpoint()</c>。
/// </summary>
public sealed class RequestOpsFeature
{
    public const string Unmatched = "(unmatched)";

    public required string Route { get; init; }
    public string? ModuleKey { get; init; }

    public static RequestOpsFeature From(HttpContext ctx)
    {
        var route = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? Unmatched;
        string? moduleKey = null;
        if (route.Contains("modules/{key}", StringComparison.Ordinal) && ctx.Request.RouteValues.TryGetValue("key", out var key))
            moduleKey = key?.ToString();
        return new RequestOpsFeature { Route = route, ModuleKey = moduleKey };
    }
}
