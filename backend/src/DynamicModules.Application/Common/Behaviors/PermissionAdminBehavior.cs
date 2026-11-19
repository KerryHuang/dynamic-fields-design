using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 宣告 <see cref="IPermissionAdminRequest"/> 的請求要有「權限設定」（權限軸設計 §7.1）；通過後整段 next()（交易、存檔、提交、稽核都在裡面）
/// 在 Authorized 寫入許可內執行。同時是系統管理請求時許可帶 SystemAdminVerified（SystemAdminBehavior 已在前面擋過）。
/// </summary>
public class PermissionAdminBehavior<TReq, TRes>(PermissionAdminGate gate) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IPermissionAdminRequest) return await next();
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是權限設定請求，回傳型別必須是 ServiceResult（fail-closed）");

        return await gate.RunAsync(() => next(), ct, systemAdminVerified: request is ISystemAdminRequest);
    }
}
