using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

using DynamicModules.Application.Common;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>Outcome：submitted（建立實例）、approved（全部關卡都被跳過，直接核准）、direct（沒有流程符合，直接轉移）。</summary>
public sealed record SubmitApprovalResponse(string Outcome, long? InstanceId, string RecordRowVersion);

/// <summary>Outcome：advanced、approved、rerouted、rejected、returned、withdrawn。NeedsResubmit：rerouted 且新流程要發起人重新自選，沒有新實例。</summary>
public sealed record ApprovalActionResponse(string Outcome, long InstanceId, string InstanceRowVersion, I18nText? NewFlowName = null, bool NeedsResubmit = false);

public static class ApprovalOutcomes
{
    public const string Submitted = "submitted", Direct = "direct", Advanced = "advanced", Approved = "approved", Rerouted = "rerouted",
        Rejected = "rejected", Returned = "returned", Withdrawn = "withdrawn";
}

/// <summary>動作的共同規則（設計 §7.1、§7.7）。</summary>
public static class ApprovalActionRules
{
    /// <summary>空白算沒填；回 CommentRequired／CommentTooLong／null。</summary>
    public static MessageRef? CommentError(string? comment, bool required, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(comment)) return required ? ApprovalMessages.CommentRequired : null;
        return comment.Length > maxLength ? ApprovalMessages.CommentTooLong(maxLength) : null;
    }

    /// <summary>
    /// 「上一關」（設計 §7.3）：依流程定義的關卡順序，目前關卡代號之前最近一個沒被跳過的關卡，取它最近一次的關卡實例，
    /// 必須是已同意才算；第一關、備援關卡（不在定義裡）沒有上一關。退回上一關重啟的關卡有新 seq，所以不能只比 seq。
    /// </summary>
    public static Domain.Entities.Dynamic.ApprovalStageRun? PreviousRun(
        Domain.Approvals.ApprovalDefinition? def, IReadOnlyList<Domain.Entities.Dynamic.ApprovalStageRun> runs, Domain.Entities.Dynamic.ApprovalStageRun current)
    {
        if (def is null) return null;
        var index = def.Stages.FindIndex(s => s.Code == current.StageCode);
        for (var i = index - 1; i >= 0; i--)
        {
            var latest = runs.Where(r => r.StageCode == def.Stages[i].Code).MaxBy(r => r.Seq);
            if (latest is null or { Status: Domain.Entities.Dynamic.ApprovalStageStatus.Skipped }) continue;
            return latest.Status == Domain.Entities.Dynamic.ApprovalStageStatus.Approved ? latest : null;
        }
        return null;
    }

    /// <summary>Assignee，或 Reason = delegated 時的 Original（a3 用）。</summary>
    public static bool IsActor(Domain.Entities.Dynamic.ApprovalTask t, string userId) =>
        t.AssigneeUserId == userId || (t.Reason == Domain.Entities.Dynamic.ApprovalTaskReason.Delegated && t.OriginalUserId == userId);
}

// ── 查詢（設計 §10.2、§13.3、§13.4、§15） ──

/// <summary>記錄詳情的簽核摘要（GET records/{id} 的 approval）。Status：最近一個實例的狀態，沒有實例但能送出時是 none。</summary>
public sealed record RecordApprovalSummary(string Status, I18nText? StageName, int? StageSeq, bool CanSubmit);

/// <summary>記錄處於簽核鎖定狀態時，讀者看得到的鎖定欄位 key 與明細列是否一起鎖（GET records/{id} 的 approvalLock）。</summary>
public sealed record RecordApprovalLock(IReadOnlyList<string> FieldKeys, bool DetailRowsLocked);

public sealed record ApprovalTaskView(long Id, string AssigneeUserId, string AssigneeName, string OriginalUserId, string Reason, string Status,
    DateTime? ActedTime, bool CanAct, bool CanReturnPrevious = false);

public sealed record ApprovalStageView(long Id, int Seq, string? StageCode, I18nText Name, string Mode, string Status, string? SkipReason,
    DateTime? DueTime, IReadOnlyList<ApprovalTaskView> Tasks, IReadOnlyList<string> EditableFieldKeys);

public sealed record ApprovalActionView(string Action, string ActorUserId, string ActorName, string? OnBehalfOfUserId, IReadOnlyList<string> TargetUserIds,
    string? Comment, JsonElement? Data, DateTime CreatedTime);

public sealed record ApprovalInstanceView(long Id, string Status, string RouteKind, I18nText? FlowName, long FromOptionId, long ToOptionId,
    string InitiatorUserId, DateTime SubmittedTime, DateTime? CompletedTime, string RowVersion, IReadOnlyList<ApprovalStageView> Stages,
    IReadOnlyList<ApprovalActionView> Actions, bool CanWithdraw, bool CanCancel, string TitleSnapshot);

/// <summary>Current：進行中的實例；Previous：其他看得到的實例（送出時間降冪）；SubmitTargets：現在能送簽的目標狀態。</summary>
public sealed record RecordApprovalResponse(ApprovalInstanceView? Current, IReadOnlyList<ApprovalInstanceView> Previous, IReadOnlyList<long> SubmitTargets);

/// <summary>簽核中心的一列。TaskId：todo／done 是任務，submitted／all 是 0（以實例為單位）。Status 一律是實例狀態。</summary>
public sealed record ApprovalInboxItem(long TaskId, long InstanceId, long RecordId, string ModuleKey, I18nText ModuleName, string TitleSnapshot,
    I18nText? StageName, string InitiatorUserId, string InitiatorName, DateTime SubmittedTime, DateTime? DueTime, bool Overdue, bool ModuleClosed,
    string Status);

public static class ApprovalInboxBoxes
{
    public const string Todo = "todo", Done = "done", Submitted = "submitted", All = "all";
    public static readonly IReadOnlySet<string> Values = new HashSet<string> { Todo, Done, Submitted, All };
}

public static class ApprovalInstanceStatuses
{
    public static readonly IReadOnlySet<string> Values = new HashSet<string>
    {
        Domain.Entities.Dynamic.ApprovalInstanceStatus.Pending, Domain.Entities.Dynamic.ApprovalInstanceStatus.Approved,
        Domain.Entities.Dynamic.ApprovalInstanceStatus.Rejected, Domain.Entities.Dynamic.ApprovalInstanceStatus.Withdrawn,
        Domain.Entities.Dynamic.ApprovalInstanceStatus.Cancelled, Domain.Entities.Dynamic.ApprovalInstanceStatus.Rerouted
    };
}

// ── 預覽與試算（設計 §5.4） ──

/// <summary>Status：enter（會進入）、skip（跳過，只在設計器試算）、conditional（條件引用對發起人隱藏的欄位）、pick（需發起人自選）。</summary>
public sealed record PreviewStage(string? Code, I18nText Name, string Status, IReadOnlyList<UserRef> Approvers, bool UsesFallback);

/// <summary>Outcome：flow（會用 FlowName 這個流程）、direct（沒有流程符合、直接轉移）、blocked（沒有流程符合、送出會被擋）。</summary>
public sealed record ApprovalPreviewResponse(string Outcome, I18nText? FlowName, IReadOnlyList<PreviewStage> Stages, IReadOnlyList<PickRequirement> Picks);

public sealed record UserRef(string UserId, string DisplayName);

public sealed record PickRequirement(string StageCode, I18nText Name, int Min, int Max, IReadOnlyList<UserRef> Candidates);

public static class ApprovalPreviewOutcomes
{
    public const string Flow = "flow", Direct = "direct", Blocked = "blocked";
}

public static class PreviewStatuses
{
    public const string Enter = "enter", Skip = "skip", Conditional = "conditional", Pick = "pick";
}
