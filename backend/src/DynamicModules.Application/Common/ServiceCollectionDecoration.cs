using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Common;

internal static class ServiceCollectionDecoration
{
    /// <summary>
    /// 把最後一筆非 keyed 的 <typeparamref name="TService"/> 換成「建原實作再包裝飾器」，lifetime 不變。
    /// 內層直接用原註冊的 instance、factory 或 type 建，不從容器解析 <typeparamref name="TService"/>，所以不會遞迴解析到自己。
    /// 找不到註冊丟 <see cref="InvalidOperationException"/>（<paramref name="caller"/> 寫進訊息）。
    /// </summary>
    public static void Decorate<TService>(IServiceCollection services, Func<IServiceProvider, TService, TService> wrap, string caller)
        where TService : class
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService) && !services[i].IsKeyedService) { index = i; break; }
        }
        if (index < 0)
            throw new InvalidOperationException($"呼叫 {caller} 前必須先註冊 {typeof(TService).Name}");

        var original = services[index];
        Func<IServiceProvider, object> createInner = original switch
        {
            { ImplementationInstance: { } instance } => _ => instance,
            { ImplementationFactory: { } factory } => factory,
            { ImplementationType: { } type } => sp => ActivatorUtilities.CreateInstance(sp, type),
            _ => throw new InvalidOperationException($"無法裝飾 {typeof(TService).Name} 的註冊方式")
        };
        services[index] = ServiceDescriptor.Describe(
            typeof(TService), sp => wrap(sp, (TService)createInner(sp)), original.Lifetime);
    }
}
