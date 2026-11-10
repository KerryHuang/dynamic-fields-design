using System.Text.Json;
using DynamicModules.Application.Common;
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

/// <summary>Box：todo、done、submitted、all；From／To 篩送出日（含頭尾）；ModuleKey 為 null 不限模組。</summary>
public record GetApprovalTasksRequest(string Box, string? ModuleKey, DateOnly? From, DateOnly? To, int CurrentPage, int PagesSize)
    : IRequest<ServiceResult>;
