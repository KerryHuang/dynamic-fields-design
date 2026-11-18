using DynamicModules.Application.Common.Audit;

namespace DynamicModules.WebAPI.Security;

/// <summary>稽核的來源 IP：沿用限流用的 <see cref="ClientIpResolver"/>（維運設計 §17.2.1）；沒有 HttpContext（背景工作）時為 null。</summary>
public sealed class HttpRequestOrigin(IHttpContextAccessor accessor, ClientIpResolver resolver) : IRequestOrigin
{
    public string? ClientIp => accessor.HttpContext is { } http ? resolver.Resolve(http) : null;
}
