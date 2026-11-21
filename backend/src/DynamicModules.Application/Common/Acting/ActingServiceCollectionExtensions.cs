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
        ServiceCollectionDecoration.Decorate<ICurrentUser>(services, (_, inner) => new ActingCurrentUser(inner), nameof(AddActingOverrides));
        ServiceCollectionDecoration.Decorate<IBusinessTime>(services, (_, inner) => new ActingBusinessTime(inner), nameof(AddActingOverrides));
        return services;
    }
}
