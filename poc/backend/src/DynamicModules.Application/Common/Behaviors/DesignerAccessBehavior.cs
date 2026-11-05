using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

public class DesignerAccessBehavior<TReq, TRes>(IDynamicAccessGuard guard) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IDesignerRequest designer) return await next();
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是設計器請求，回傳型別必須是 ServiceResult（fail-closed）");

        var actions = await guard.DesignerActionsAsync(ct);
        return actions.Contains(designer.RequiredDesignerAction)
            ? await next()
            : (TRes)(object)ServiceResult.Forbidden();
    }
}
