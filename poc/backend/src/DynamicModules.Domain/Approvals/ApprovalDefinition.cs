using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Approvals;

// 簽核流程定義（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3）。
// 存在 Dynamic_ApprovalFlowVersion.Definition 與實例的流程快照裡，版本一經使用就不再修改。

/// <summary>流程定義：形狀、啟動條件、退回狀態、備援簽核人與依序的關卡。</summary>
public sealed record ApprovalDefinition(
    string Shape, JsonElement? When, long? RejectTo, ApproverSource? FallbackApprovers, List<ApprovalStageDef> Stages);

/// <summary>一個關卡：代號、名稱、條件、簽核人來源、會簽或或簽、可編輯欄位、逾期時數、找不到人時的處理。</summary>
public sealed record ApprovalStageDef(
    string Code, I18nText Name, JsonElement? When, ApproverSource Approvers, string Mode,
    List<long> EditableFields, int? DueHours, string OnEmpty);

/// <summary>簽核人來源；依 Source 使用其中的屬性。UnmatchedGroups、CandidatesUnmatched 由開啟套件時寫入（設計 §14.4）。</summary>
public sealed record ApproverSource(
    string Source, List<string>? UserIds = null, List<long>? GroupIds = null, int? Level = null,
    long? FieldId = null, List<long>? CandidateGroupIds = null, int? Min = null, int? Max = null,
    List<string>? UnmatchedGroups = null, bool CandidatesUnmatched = false);

public static class ApprovalSources
{
    public const string Users = "users", Group = "group", Manager = "manager", UserField = "userField", InitiatorPick = "initiatorPick";
}

public static class ApprovalModes
{
    public const string All = "all", Any = "any";
}

public static class ApprovalOnEmpty
{
    public const string Fallback = "fallback", Skip = "skip";
}

public static class ApprovalShapes
{
    public const string Sequence = "sequence";
}

/// <summary>轉移的簽核設定 NoMatch 的值。</summary>
public static class ApprovalNoMatch
{
    public const string Block = "block", Direct = "direct";
}

/// <summary>實例的流程快照：送出時目標狀態上每個流程的版本、優先序、啟停與完整定義（設計 §4.4）。</summary>
public sealed record FlowSnapshotEntry(long FlowId, long FlowVersionId, int Priority, bool Active, I18nText Name, ApprovalDefinition Definition);

/// <summary>設計 §4.3、§6.1、§7.1、§7.6 的上限。</summary>
public static class ApprovalLimits
{
    public const int MaxStages = 10;
    public const int MaxApprovers = 20;
    public const int MaxManagerLevel = 5;
    public const int MaxDueHours = 8760;
    public const int MaxCommentLength = 1000;
}

/// <summary>流程與它目前版本的定義；入口約束與鎖定計算的輸入。</summary>
public sealed record FlowState(Entities.Dynamic.ApprovalFlow Flow, ApprovalDefinition Definition);
