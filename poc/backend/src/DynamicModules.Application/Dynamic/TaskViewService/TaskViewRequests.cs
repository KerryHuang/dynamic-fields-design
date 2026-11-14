using System.Text.Json;
using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskViewService;

/// <summary>Kind 這一段只收 board（甘特圖在 gt1 開放）。成功時 Data 是 <see cref="TaskViewListResponse"/>。</summary>
public sealed record GetTaskViewsRequest(string? Kind) : IRequest<ServiceResult>;

/// <summary>Config 是 <see cref="Domain.Tasks.TaskBoardConfig"/> 的 JSON；沒給時存預設看板的設定。</summary>
public sealed record CreateTaskViewRequest(string? Kind, string? Name, JsonElement? Config) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record GetTaskViewRequest(long Id) : IRequest<ServiceResult>;

/// <summary>RowVersion 不符回 409（code stale）。</summary>
public sealed record UpdateTaskViewRequest(long Id, string? Name, JsonElement? Config, string? RowVersion) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record DeleteTaskViewRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>看得到就能另存：設定照抄、擁有者換成自己、不帶分享。</summary>
public sealed record CopyTaskViewRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>整批取代。Kind 是 user、group、department；Id 是使用者帳號或群組、部門的 Id。</summary>
public sealed record SetTaskViewSharesRequest(long Id, IReadOnlyList<TaskViewShareInput> Shares) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record TaskViewShareInput(string? Kind, string? Id);

/// <summary>分享對話框的選項：啟用中的使用者、啟用中的部門、群組（只有 Id 與名稱）。任何登入者可讀。</summary>
public sealed record GetTaskViewShareTargetsRequest : IRequest<ServiceResult>;
