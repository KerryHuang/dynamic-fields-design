using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

// 簽核的資料表（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3）。

/// <summary>簽核流程：掛在模組狀態機的目標狀態上；Key 全站唯一。</summary>
public class ApprovalFlow : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long ModuleId { get; set; }
    public long ToOptionId { get; set; }
    public int Priority { get; set; }
    public bool Active { get; set; } = true;
    public long? CurrentVersionId { get; set; }
}

/// <summary>流程版本：被實例綁定或出現在快照裡之後不再修改。</summary>
public class ApprovalFlowVersion
{
    public long Id { get; set; }
    public long FlowId { get; set; }
    public int Version { get; set; }
    public ApprovalDefinition Definition { get; set; } = null!;
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedTime { get; set; }
}

/// <summary>一次送簽。一筆記錄同時最多一個 pending 的實例（部分唯一索引）。</summary>
public class ApprovalInstance
{
    public long Id { get; set; }
    public long RecordId { get; set; }
    public long ModuleId { get; set; }
    public long? FlowId { get; set; }
    public long? FlowVersionId { get; set; }
    public List<FlowSnapshotEntry> FlowSetSnapshot { get; set; } = [];
    public string RouteKind { get; set; } = ApprovalRouteKind.Flow;
    public long FromOptionId { get; set; }
    public long ToOptionId { get; set; }
    public string InitiatorUserId { get; set; } = "";
    public string Status { get; set; } = ApprovalInstanceStatus.Pending;
    public long? RestartedFromInstanceId { get; set; }
    public long? CurrentStageRunId { get; set; }
    public string TitleSnapshot { get; set; } = "";
    public Dictionary<string, List<string>> Picks { get; set; } = [];
    public DateTime SubmittedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public string RowVersion { get; set; } = "";
    public string OperationId { get; set; } = "";
}

/// <summary>流程快照的索引列：判斷流程或版本「被用過」時用，不必掃 JSON。</summary>
public class ApprovalInstanceFlow
{
    public long InstanceId { get; set; }
    public long FlowId { get; set; }
    public long FlowVersionId { get; set; }
}

/// <summary>關卡實例；退回重來會產生新列。StageCode 為 null 是加簽關卡。</summary>
public class ApprovalStageRun
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public int Seq { get; set; }
    public string? StageCode { get; set; }
    public I18nText? NameSnapshot { get; set; }
    public string Mode { get; set; } = ApprovalModes.Any;
    public string Status { get; set; } = ApprovalStageStatus.Queued;
    public string? SkipReason { get; set; }
    public string? AddedByUserId { get; set; }
    public List<string>? AddSignUserIds { get; set; }
    public DateTime? ActivatedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public DateTime? DueTime { get; set; }
    public string? NodeId { get; set; }
    public string? BranchKey { get; set; }
}

/// <summary>指派給一位使用者的待簽核。</summary>
public class ApprovalTask
{
    public long Id { get; set; }
    public long StageRunId { get; set; }
    public long InstanceId { get; set; }
    public long RecordId { get; set; }
    public string AssigneeUserId { get; set; } = "";
    public string OriginalUserId { get; set; } = "";
    public string SeatKey { get; set; } = "";
    public string Reason { get; set; } = ApprovalTaskReason.Resolved;
    public string Status { get; set; } = ApprovalTaskStatus.Open;
    public DateTime CreatedTime { get; set; }
    public DateTime? ActedTime { get; set; }
    public DateTime? DueTime { get; set; }
    public DateTime? LastRemindedTime { get; set; }
}

/// <summary>簽核歷程：只新增不修改。</summary>
public class ApprovalAction
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public long? StageRunId { get; set; }
    public long? TaskId { get; set; }
    public string Action { get; set; } = "";
    public string ActorUserId { get; set; } = "";
    public string? OnBehalfOfUserId { get; set; }
    public List<string>? TargetUserIds { get; set; }
    public string? Comment { get; set; }
    public string? DataJson { get; set; }
    public DateTime CreatedTime { get; set; }
    public string OperationId { get; set; } = "";
}

/// <summary>代理設定：一人一筆，期間以營運日判斷、頭尾都含。</summary>
public class ApprovalDelegate
{
    public string UserId { get; set; } = "";
    public string DelegateUserId { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}

public static class ApprovalInstanceStatus
{
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected", Withdrawn = "withdrawn",
        Cancelled = "cancelled", Rerouted = "rerouted";
}

public static class ApprovalStageStatus
{
    public const string Queued = "queued", Active = "active", Approved = "approved", Skipped = "skipped", Returned = "returned", Closed = "closed";
}

public static class ApprovalTaskStatus
{
    public const string Open = "open", Approved = "approved", Rejected = "rejected", Transferred = "transferred", Reassigned = "reassigned", Closed = "closed";
}

public static class ApprovalTaskReason
{
    public const string Resolved = "resolved", Delegated = "delegated", Transferred = "transferred", Reassigned = "reassigned", Added = "added", Fallback = "fallback";
}

public static class ApprovalActions
{
    public const string Submit = "submit", Approve = "approve", Reject = "reject", Return = "return", Withdraw = "withdraw",
        Transfer = "transfer", AddSign = "add-sign", Skip = "skip", Cancel = "cancel", Complete = "complete", Reroute = "reroute",
        ContentChanged = "content-changed", Fallback = "fallback", NotDelegated = "not-delegated", Reassign = "reassign";
}

public static class ApprovalRouteKind
{
    public const string Flow = "flow", Fallback = "fallback";
}
