using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Common.Acting;

public static class ActingServiceCollectionExtensions
{
    /// <summary>
    /// 把最後一筆 ICurrentUser、IBusinessTime 換成「解析原實作再包 Acting 裝飾器」，lifetime 不變。
    /// 須在兩者都註冊之後呼叫。
    /// </summary>
    public static IServiceCollection AddActingOverrides(this IServiceCollection services)
    {
        Decorate<ICurrentUser>(services, inner => new ActingCurrentUser(inner));
        Decorate<IBusinessTime>(services, inner => new ActingBusinessTime(inner));
        return services;
    }

    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> wrap)
        where TService : class
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService) && !services[i].IsKeyedService) { index = i; break; }
        }
        if (index < 0)
            throw new InvalidOperationException($"呼叫 AddActingOverrides 前必須先註冊 {typeof(TService).Name}");

        var original = services[index];
        Func<IServiceProvider, object> createInner = original switch
        {
            { ImplementationInstance: { } instance } => _ => instance,
            { ImplementationFactory: { } factory } => factory,
            { ImplementationType: { } type } => sp => ActivatorUtilities.CreateInstance(sp, type),
            _ => throw new InvalidOperationException($"無法裝飾 {typeof(TService).Name} 的註冊方式")
        };
        services[index] = ServiceDescriptor.Describe(
            typeof(TService), sp => wrap((TService)createInner(sp)), original.Lifetime);
    }
}
