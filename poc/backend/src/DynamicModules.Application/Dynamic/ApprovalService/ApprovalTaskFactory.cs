using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>依席位建任務（a3 會在這裡加代理）。</summary>
public class ApprovalTaskFactory(IApprovalInstanceRepository repo, IBusinessTime time)
{
    public virtual Task<List<ApprovalTask>> CreateAsync(ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, CancellationToken ct)
    {
        var tasks = new List<ApprovalTask>();
        foreach (var seat in seats)
            foreach (var userId in seat.UserIds)
            {
                var task = new ApprovalTask
                {
                    StageRunId = run.Id, InstanceId = inst.Id, RecordId = inst.RecordId, AssigneeUserId = userId, OriginalUserId = userId,
                    SeatKey = seat.SeatKey, Reason = seat.Reason, Status = ApprovalTaskStatus.Open, CreatedTime = time.Now, DueTime = run.DueTime
                };
                repo.AddTask(task);
                tasks.Add(task);
            }
        return Task.FromResult(tasks);
    }
}
