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
/// Cascade（complete、cancel）同 PATCH，只在依狀態分欄時有作用。成功時 Data 是 <see cref="TaskMoveResponse"/>。
/// </summary>
public sealed record MoveTaskRequest(long Id, string? GroupBy, string? ToColumn, DateOnly? DueDate, string? ExpectedVersion, string? Cascade = null)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>
/// 甘特圖拖拉與日期對話框改日期（看板設計 §11.5.2.6，GT-T7）：兩個日期一律都送，null 代表清除；ExpectedVersion 是卡片的 version，不符 409。
/// 成功時 Data 是 <see cref="TaskScheduleResponse"/>。
/// </summary>
public sealed record ScheduleTaskRequest(long Id, DateOnly? StartDate, DateOnly? DueDate, string? ExpectedVersion)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>甘特查詢的時間窗（含頭含尾）；請求裡兩個都要有，沒帶整個 window 時伺服器依刻度給預設。</summary>
public sealed record TaskGanttWindowInput(DateOnly? From, DateOnly? To);

/// <summary>
/// 甘特查詢（看板設計 §11.5.2.5）：ViewId（看得到的存檔甘特圖）與 Config（未存的甘特設定）二擇一，都沒帶是預設甘特圖；
/// Window 選填。成功時 Data 是 <see cref="TaskGanttResponse"/>。
/// </summary>
public sealed record GetTaskGanttRequest(long? ViewId, JsonElement? Config, TaskGanttWindowInput? Window) : IRequest<ServiceResult>;
