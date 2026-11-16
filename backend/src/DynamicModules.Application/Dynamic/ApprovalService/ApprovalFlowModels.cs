using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

public sealed record ApprovalFlowInput(string Key, I18nText Name, long ToOptionId, int Priority, bool Active, ApprovalDefinition Definition);

/// <summary>
/// Version 是目前版本號，沒有已發布版本的流程（只有草稿）為 null，VersionId、Definition 同；Draft 是設計器的草稿（沒有或讀不懂時為 null）；
/// Used：任何版本被實例綁定或出現在快照裡；PendingCount：綁定這個流程的進行中實例數；CurrentVersionUsed：目前版本是否已被用過；
/// 用過而且定義有改的存檔才會產生新版本（只改名稱、啟停、優先序不會）。
/// 設計器欄位（J64、J65）：Graph 只有單筆 GET 帶、而且只有目前版本是 sequence 時才有（轉換後的直線圖）；Layout 是目前版本的節點座標；
/// DraftRev 是樂觀鎖權杖（發布、捨棄後沒有草稿內容也有值；從沒存過草稿為 null）；DraftUpdatedBy／ByName／Time 是草稿最後的修改者與時間。
/// </summary>
public sealed record ApprovalFlowResponse(
    string Key, I18nText Name, string ModuleKey, long ToOptionId, int Priority, bool Active,
    int? Version, bool Used, int PendingCount, ApprovalDefinition? Definition, bool CurrentVersionUsed, ApprovalFlowDraftView? Draft = null,
    long? VersionId = null, ApprovalDefinition? Graph = null, ApprovalFlowLayout? Layout = null, string? DraftRev = null,
    string? DraftUpdatedBy = null, string? DraftUpdatedByName = null, DateTime? DraftUpdatedTime = null);

/// <summary>草稿的內容：定義與版面（流程列屬性不進草稿，§10.8）。</summary>
public sealed record ApprovalFlowDraftView(ApprovalDefinition Definition, ApprovalFlowLayout? Layout);

/// <summary>草稿衝突（409）帶回的資訊：目前草稿是誰、何時存的（沒有草稿時是流程最後的修改者與時間），前端據以提示「覆蓋」。放在 409 本文的 params。</summary>
public sealed record DraftConflictInfo(string? DraftUpdatedBy, string? DraftUpdatedByName, DateTime? DraftUpdatedTime, string? DraftRev, long? VersionId);

/// <summary>發布的回應：發布後的流程與警告 W1–W3。</summary>
public sealed record ApprovalFlowPublishResponse(ApprovalFlowResponse Flow, IReadOnlyList<ApprovalFlowWarning> Warnings);

/// <summary>
/// 設計器的草稿、乾跑驗證與發布的請求本文（J64）。Layout 收原始 JSON，由 <see cref="ApprovalFlowLayout"/> 驗格式（不合法回 400，不是模型繫結錯誤）。
/// BaseRev、BaseVersionId 是讀到草稿時的權杖與目前版本（樂觀鎖，J65），新流程為 null；Force 為 true 時覆蓋讀取之後才進來的變更之前的內容。
/// </summary>
public sealed record ApprovalFlowDraftInput(
    string Key, I18nText Name, long ToOptionId, int Priority, ApprovalDefinition Definition, JsonElement? Layout = null,
    string? BaseRev = null, long? BaseVersionId = null, bool Force = false)
{
    public ApprovalFlowInput ToFlowInput(bool active) => new(Key, Name, ToOptionId, Priority, active, Definition);
}

/// <summary>警告 W1–W3（顯示但不擋發布，§9）：Key 與錯誤鍵同格式（definition.nodes.{id}），Code 是規則編號。</summary>
public sealed record ApprovalFlowWarning(string Key, string Code, string Message);

/// <summary>
/// 乾跑驗證的回應：Errors 與發布會回 400 的錯誤相同；有錯誤時 Warnings 為空。
/// NewVersion：目前版本被用過、而且這份定義與它不同，發布會開新版本（NextVersion 是新版號）；PendingCount 是綁定這條流程的進行中單據數。
/// </summary>
public sealed record ApprovalFlowValidation(
    IReadOnlyDictionary<string, string[]> Errors, IReadOnlyList<ApprovalFlowWarning> Warnings, bool NewVersion, int? NextVersion, int PendingCount);

/// <summary>流程檢查的結果：失敗時是 400 的 <see cref="ServiceResult"/>，通過時帶警告。</summary>
public sealed record FlowCheck(ServiceResult? Failure, IReadOnlyList<ApprovalFlowWarning> Warnings);
