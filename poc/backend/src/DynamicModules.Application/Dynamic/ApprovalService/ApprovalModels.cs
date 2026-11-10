namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>Outcome：submitted（建立實例）、approved（全部關卡都被跳過，直接核准）、direct（沒有流程符合，直接轉移）。</summary>
public sealed record SubmitApprovalResponse(string Outcome, long? InstanceId, string RecordRowVersion);

/// <summary>Outcome：advanced、approved、rerouted、rejected、returned、withdrawn。</summary>
public sealed record ApprovalActionResponse(string Outcome, long InstanceId, string InstanceRowVersion, string? NewFlowName = null);

public static class ApprovalOutcomes
{
    public const string Submitted = "submitted", Direct = "direct", Advanced = "advanced", Approved = "approved", Rerouted = "rerouted",
        Rejected = "rejected", Returned = "returned", Withdrawn = "withdrawn";
}

/// <summary>動作的共同規則（設計 §7.1、§7.7）。</summary>
public static class ApprovalActionRules
{
    /// <summary>空白算沒填；回 CommentRequired／CommentTooLong／null。</summary>
    public static string? CommentError(string? comment, bool required)
    {
        if (string.IsNullOrWhiteSpace(comment)) return required ? ApprovalMessages.CommentRequired : null;
        return comment.Length > Domain.Approvals.ApprovalLimits.MaxCommentLength ? ApprovalMessages.CommentTooLong : null;
    }

    /// <summary>Assignee，或 Reason = delegated 時的 Original（a3 用）。</summary>
    public static bool IsActor(Domain.Entities.Dynamic.ApprovalTask t, string userId) =>
        t.AssigneeUserId == userId || (t.Reason == Domain.Entities.Dynamic.ApprovalTaskReason.Delegated && t.OriginalUserId == userId);
}
