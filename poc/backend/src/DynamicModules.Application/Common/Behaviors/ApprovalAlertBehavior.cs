using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 排在 Transaction 之外（技術決定 8）：請求失敗、主交易已回滾，而且有待送的簽核警示時，另送一個交易型請求通知系統管理員。
/// 送之前丟掉失敗請求留在 ChangeTracker 的修改，免得跟著警示一起寫進去。
/// </summary>
public class ApprovalAlertBehavior<TReq, TRes>(ApprovalAlert alert, ISender sender, IUnitOfWork uow) : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        var response = await next();
        if (request is WriteApprovalAlertRequest || response is not ServiceResult { IsSuccess: false } || alert.Pending is not { } pending) return response;
        alert.Clear();
        uow.Discard();
        await sender.Send(new WriteApprovalAlertRequest(pending.InstanceId, pending.Reason), ct);
        return response;
    }
}
