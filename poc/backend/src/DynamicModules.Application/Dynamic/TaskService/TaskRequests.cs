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
