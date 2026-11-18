using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 管理與設計類請求的稽核（維運設計 §17.2.1、O2-T2）：在 <see cref="TransactionBehavior{TReq,TRes}"/> 之內、
/// <see cref="IdempotencyBehavior{TReq,TRes}"/> 之外。請求是 <see cref="IAuditedRequest"/> 且結果成功時寫一列、再存檔一次
/// （交易還沒提交，所以和業務寫入同交易）；稽核寫不進去就整筆失敗（§5.6）。只記成功：被權限擋下、驗證失敗的不記（O2-U9）。
/// 巢狀的稽核請求只由最外層寫一列（<see cref="AuditContext"/>）。
/// </summary>
public class AuditBehavior<TReq, TRes>(AuditContext context, IAuditLog audit, IUnitOfWork uow) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IAuditedRequest audited) return await next();
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 有稽核宣告，回傳型別必須是 ServiceResult");

        context.Depth++;
        try
        {
            var response = await next();
            if (context.Depth != 1 || response is not ServiceResult { IsSuccess: true }) return response;
            var action = audited.Audit;
            audit.Add(new AuditEntry(action.Code, AuditResult.Success, action.TargetKind,
                context.TargetId ?? action.TargetId, context.TargetLabel ?? action.TargetLabel, Summary: context.Summary?.ToJson()));
            await uow.CommitAsync(ct);
            return response;
        }
        finally
        {
            if (--context.Depth == 0) context.Reset();
        }
    }
}
