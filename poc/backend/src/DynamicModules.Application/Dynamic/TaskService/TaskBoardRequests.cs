using System.Text.Json;
using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 看板各欄的總數與第一頁卡片。ViewId（看得到的存檔看板）與 Config（未存的設定）二擇一；兩者都沒帶時是預設看板。
/// 成功時 Data 是 <see cref="TaskBoardResponse"/>。
/// </summary>
public sealed record GetTaskBoardColumnsRequest(long? ViewId, JsonElement? Config) : IRequest<ServiceResult>;

/// <summary>同一欄的下一頁（每頁 50 張，最多到第 500 張）。成功時 Data 是 <see cref="TaskBoardPageResponse"/>。</summary>
public sealed record GetTaskBoardColumnPageRequest(string Column, long? ViewId, JsonElement? Config, int Offset) : IRequest<ServiceResult>;

/// <summary>
/// 拖到某欄：伺服器依欄算新值（看板設計 §5.3），ExpectedVersion 是卡片載入時的 version，不符 409。DueDate 只用於「之後」欄。
/// 成功時 Data 是 <see cref="TaskMoveResponse"/>。
/// </summary>
public sealed record MoveTaskRequest(long Id, string? GroupBy, string? ToColumn, DateOnly? DueDate, string? ExpectedVersion)
    : IRequest<ServiceResult>, ITransactionalRequest;
