using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

public class EnsureScheduledJobHandler(IScheduledJobRepository jobs, IUnitOfWork uow, IDatabaseErrors errors)
    : IRequestHandler<EnsureScheduledJobRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureScheduledJobRequest request, CancellationToken ct)
    {
        if (await jobs.ExistsAsync(request.JobKey, ct)) return ServiceResult.Success();
        jobs.Add(new ScheduledJob { JobKey = request.JobKey, NextDueUtc = request.NextDueUtc });
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Exception e) when (errors.IsUniqueViolation(e))
        {
            // 多實例同時啟動：別的實例先插了。PostgreSQL 的交易在 INSERT 失敗後不能再用，回非成功讓交易回滾
            return ServiceResult.Conflict(null);
        }
        return ServiceResult.Success();
    }
}

public class AcquireJobLeaseHandler(IScheduledJobRepository jobs) : IRequestHandler<AcquireJobLeaseRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AcquireJobLeaseRequest r, CancellationToken ct) =>
        ServiceResult.Success(await jobs.TryAcquireAsync(r.JobKey, r.Owner, r.NowUtc, r.LeaseUntilUtc, ct));
}

public class RenewJobLeaseHandler(IScheduledJobRepository jobs) : IRequestHandler<RenewJobLeaseRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RenewJobLeaseRequest r, CancellationToken ct) =>
        ServiceResult.Success(await jobs.RenewAsync(r.JobKey, r.Owner, r.LeaseUntilUtc, ct));
}

public class FinishJobRunHandler(IScheduledJobRepository jobs) : IRequestHandler<FinishJobRunRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(FinishJobRunRequest r, CancellationToken ct) =>
        ServiceResult.Success(await jobs.FinishAsync(r.JobKey, r.Owner, r.Result, ct));
}
