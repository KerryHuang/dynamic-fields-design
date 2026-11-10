using System.Text.Json;
using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>個人任務（不掛記錄）。</summary>
public sealed record CreateTaskRequest(TaskInput Input) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record CreateRecordTaskRequest(long RecordId, TaskInput Input) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>Patch 是原始 JSON：沒送的欄位不改，送 null 代表清除。</summary>
public sealed record UpdateTaskRequest(long Id, JsonElement Patch) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record DeleteTaskRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>Tab：assigned、created、mentioned；Filter：open、overdue、thisWeek、all。</summary>
public sealed record GetMyTasksRequest(string Tab, string Filter, int CurrentPage, int PagesSize) : IRequest<ServiceResult>;

public sealed record GetTaskSummaryRequest : IRequest<ServiceResult>;

public sealed record GetTaskRequest(long Id) : IRequest<ServiceResult>;

public sealed record GetRecordTasksRequest(long RecordId) : IRequest<ServiceResult>;
