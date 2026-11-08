using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 寫入後的階段（設計 §2 步驟 8 到 10）。依 <see cref="Order"/> 執行：事件（8）、寫入後盾（9）、跨記錄驗證（10）。
/// 回傳非 null 就是失敗，整筆回滾（TransactionBehavior 看到非成功的 ServiceResult）。
/// </summary>
public interface IWriteStage
{
    int Order { get; }
    Task<ServiceResult?> RunAsync(CancellationToken ct);
}

/// <summary>放在 Transaction 與 Idempotency 之內，只處理 <see cref="IRecordWriteRequest"/>，對其他請求直接放行。</summary>
public class RecordEffectsBehavior<TReq, TRes>(IEnumerable<IWriteStage> stages) : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        var response = await next();
        if (request is not IRecordWriteRequest || response is not ServiceResult { IsSuccess: true }) return response;

        foreach (var stage in stages.OrderBy(s => s.Order))
            if (await stage.RunAsync(ct) is { } failure)
                return (TRes)(object)failure;
        return response;
    }
}
