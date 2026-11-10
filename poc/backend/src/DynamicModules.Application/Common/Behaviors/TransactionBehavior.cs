using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

public class TransactionBehavior<TReq, TRes>(ITransactionManager transactions, AfterCommitActions afterCommit) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not ITransactionalRequest) return await next();

        afterCommit.Enter();
        var committed = false;
        try
        {
            await using var tx = await transactions.BeginAsync(ct);
            try
            {
                var response = await next();
                if (response is ServiceResult { IsSuccess: false })
                    await tx.RollbackAsync(ct);
                else
                {
                    await tx.CommitAsync(ct);
                    committed = true;
                }
                return response;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            afterCommit.Leave(committed);
        }
    }
}
