using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 排在 Transaction 之外（技術決定 8）：請求失敗、主交易已回滾，而且有待送的簽核警示時，另送一個交易型請求通知系統管理員。
/// 送之前丟掉失敗請求留在 ChangeTracker 的修改，免得跟著警示一起寫進去。
/// </summary>
public class ApprovalAlertBehavior<TReq, TRes>(ApprovalAlert alert, ISender sender, IUnitOfWork uow, ITransactionManager transactions,
    ILogger<ApprovalAlertBehavior<TReq, TRes>> logger) : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        // 巢狀的請求（外層已開交易）不處理：丟追蹤會清掉外層還沒存檔的修改，警示也會寫進外層交易；保留 Pending 交給最外層
        var nested = transactions.IsActive;
        var response = await next();
        if (nested || request is WriteApprovalAlertRequest || response is not ServiceResult { IsSuccess: false } || alert.Pending is not { } pending)
            return response;
        alert.Clear();
        uow.Discard();
        try
        {
            await sender.Send(new WriteApprovalAlertRequest(pending.InstanceId, pending.Reason), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 警示寫不進去（例如資料庫忙碌）：記 log，使用者照樣收到原本的回應
            logger.LogError(ex, "簽核警示寫入失敗：實例 {InstanceId}，原因 {Reason}", pending.InstanceId, pending.Reason);
        }
        return response;
    }
}
