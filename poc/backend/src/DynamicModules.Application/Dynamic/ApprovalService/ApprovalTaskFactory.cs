using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>代理沒有套用的說明（寫成 not-delegated 歷程）。Reason：initiator（代理人是發起人、A12 關閉）、duplicate（代理人在這一關已有票）。</summary>
public sealed record NotDelegatedNote(string UserId, string DelegateUserId, string Reason);

public static class NotDelegatedReasons
{
    public const string Initiator = "initiator", Duplicate = "duplicate";
}

/// <summary>
/// 依席位建任務，並套用代理（設計 §7.7；計畫 U1、U8、J11）：只有系統解析出的席位（resolved、fallback）看代理設定，加簽（added）不看；
/// 轉交、改派不經過這裡。有效代理（今天在起訖內、代理人仍啟用）存在時：代理人是發起人且 A12 關閉 → 不代理；
/// 代理人在這一關已有票（這一關任何席位的本人，或已代理出去的代理人）→ 不代理；否則任務給代理人、OriginalUserId 是本人、Reason = delegated。
/// 不串代理：只查本人的代理，不再查代理人的。
/// </summary>
public class ApprovalTaskFactory(IApprovalInstanceRepository repo, IApprovalDelegateRepository delegates, ApprovalPolicies policies, IBusinessTime time)
{
    public virtual async Task<(List<ApprovalTask> Tasks, List<NotDelegatedNote> Notes)> CreateAsync(
        ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, CancellationToken ct)
    {
        static bool Delegable(SeatPlan s) => s.Reason is ApprovalTaskReason.Resolved or ApprovalTaskReason.Fallback;
        var candidates = seats.Where(Delegable).SelectMany(s => s.UserIds).Distinct(StringComparer.Ordinal).ToList();
        var active = candidates.Count == 0 ? [] : await delegates.ActiveDelegatesAsync(candidates, time.Today, ct);
        var holders = seats.SelectMany(s => s.UserIds).ToHashSet(StringComparer.Ordinal);
        bool? allowSelf = null;
        var tasks = new List<ApprovalTask>();
        var notes = new List<NotDelegatedNote>();
        foreach (var seat in seats)
            foreach (var userId in seat.UserIds)
            {
                var (assignee, reason) = (userId, seat.Reason);
                if (Delegable(seat) && active.TryGetValue(userId, out var to))
                {
                    allowSelf ??= await policies.AllowSelfApprovalAsync(inst.ModuleId, ct);
                    if (!allowSelf.Value && to == inst.InitiatorUserId) notes.Add(new(userId, to, NotDelegatedReasons.Initiator));
                    else if (!holders.Add(to)) notes.Add(new(userId, to, NotDelegatedReasons.Duplicate));
                    else (assignee, reason) = (to, ApprovalTaskReason.Delegated);
                }
                var task = new ApprovalTask
                {
                    StageRunId = run.Id, InstanceId = inst.Id, RecordId = inst.RecordId, AssigneeUserId = assignee, OriginalUserId = userId,
                    SeatKey = seat.SeatKey, Reason = reason, Status = ApprovalTaskStatus.Open, CreatedTime = time.Now, DueTime = run.DueTime
                };
                repo.AddTask(task);
                tasks.Add(task);
            }
        return (tasks, notes);
    }
}
