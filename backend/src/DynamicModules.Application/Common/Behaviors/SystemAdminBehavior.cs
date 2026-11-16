using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

public class SystemAdminBehavior<TReq, TRes>(ICurrentUser current, IAccountRepository accounts) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not ISystemAdminRequest) return await next();
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是系統管理請求，回傳型別必須是 ServiceResult（fail-closed）");

        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        return user is { IsActive: true, IsSystemAdmin: true }
            ? await next()
            : (TRes)(object)ServiceResult.Forbidden();
    }
}
