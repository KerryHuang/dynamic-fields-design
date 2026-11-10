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

public sealed record GetTaskCommentsRequest(long TaskId) : IRequest<ServiceResult>;

/// <summary>Files 是空清單時只有文字；有附件時 Body 可以是空的。</summary>
public sealed record AddTaskCommentRequest(long TaskId, string Body, IReadOnlyList<CommentFileUpload> Files)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>帶附件的留言在收進主體之前的資格檢查：看不到或不存在 404、凍結 409，其餘成功。</summary>
public sealed record CheckTaskCommentUploadRequest(long TaskId) : IRequest<ServiceResult>;

/// <summary>Content 由呼叫端（控制器）負責釋放。</summary>
public sealed record CommentFileUpload(Stream Content, string? FileName, string? ContentType);

public sealed record WithdrawTaskCommentRequest(long TaskId, long CommentId) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>啟用中的使用者，DisplayName 或 UserId 包含 Keyword（不分大小寫），最多 20 筆。</summary>
public sealed record GetMentionCandidatesRequest(string? Keyword) : IRequest<ServiceResult>;

/// <summary>成功時 Data 是 <see cref="AttachmentService.AttachmentDownload"/>。</summary>
public sealed record DownloadTaskAttachmentRequest(long TaskId, long CommentId, long AttachmentId) : IRequest<ServiceResult>;
