using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 共用主檔被多個應用程式依賴，它的設計器修改（基本欄位與貢獻欄位都算）要設計器權限再加系統管理員（共用主檔設計 §6）；
/// 設計器權限本身由 DesignerAccessBehavior 先檢查。不是主檔模組的請求照舊。
/// </summary>
public class MasterDesignerBehavior<TReq, TRes>(
    ISharedMasterRepository shared, ICurrentUser current, IAccountRepository accounts, IApprovalFlowRepository flows)
    : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IMasterDesignRequest design) return await next();
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是主檔設計器請求，回傳型別必須是 ServiceResult（fail-closed）");

        var touchesMaster = false;
        foreach (var target in design.MasterTargets)
        {
            var t = target.ApprovalFlowKey is { } flowKey ? target with { ModuleKey = await flows.ModuleKeyOfAsync(flowKey, ct) } : target;
            if (await shared.IsMasterModuleAsync(t.ModuleKey, t.FieldId, t.BlockId, t.OptionId, t.EventKey, t.CheckKey, t.ConversionKey, ct))
            {
                touchesMaster = true;
                break;
            }
        }
        if (!touchesMaster) return await next();
        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        return user is { IsActive: true, IsSystemAdmin: true }
            ? await next()
            : (TRes)(object)ServiceResult.Forbidden();
    }
}
