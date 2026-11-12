using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>送出簽核：To 是目標狀態選項 Id；Picks 是發起人自選（關卡代號 → 使用者）；不收流程鍵（設計 §5.2）。</summary>
public record SubmitApprovalRequest(long RecordId, long To, string RecordRowVersion, Dictionary<string, List<string>>? Picks, string? Comment)
    : IRequest<ServiceResult>, IRecordWriteRequest;

public record ApproveTaskRequest(long TaskId, string RowVersion, string? Comment, Dictionary<string, JsonElement?>? Values)
    : IRequest<ServiceResult>, IRecordWriteRequest;

/// <summary>To：initiator（退回發起人）或 previous（退回上一關）。</summary>
public record RejectTaskRequest(long TaskId, string RowVersion, string Comment, string To) : IRequest<ServiceResult>, IRecordWriteRequest;

public record WithdrawApprovalRequest(long InstanceId, string RowVersion, string? Comment) : IRequest<ServiceResult>, IRecordWriteRequest;

public record GetRecordApprovalRequest(long RecordId) : IRequest<ServiceResult>;

/// <summary>Box：todo、done、submitted、all；From／To 篩送出日（含頭尾）；ModuleKey 為 null 不限模組；Status 為實例狀態，null 不限。</summary>
public record GetApprovalTasksRequest(string Box, string? ModuleKey, DateOnly? From, DateOnly? To, int CurrentPage, int PagesSize, string? Status = null)
    : IRequest<ServiceResult>;

/// <summary>送出前預覽（設計 §5.4）：資格同送出，不寫入。</summary>
public record PreviewRecordApprovalRequest(long RecordId, long To) : IRequest<ServiceResult>;

/// <summary>設計器試算：以 InitiatorUserId 當發起人、Values 覆蓋記錄值（RecordId 省略時只用 Values），比對這個流程所在目標狀態的全部流程。</summary>
public record PreviewApprovalFlowRequest(string Key, long? RecordId, Dictionary<string, JsonElement?>? Values, string InitiatorUserId)
    : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
