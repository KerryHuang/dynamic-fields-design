using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>上傳一個檔案到記錄的附件欄位。只能對已存檔的記錄上傳（檔案參照靠記錄的值，沒有「暫存區」）。</summary>
public sealed record UploadAttachmentRequest(long RecordId, long FieldId, Stream Content, string? FileName)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record UploadedAttachment(string Id, string Name, long Size, string RowVersion);

public sealed record DownloadAttachmentRequest(long RecordId, long FieldId, string FileId) : IRequest<ServiceResult>;

/// <summary>下載結果。Stream 由呼叫端（控制器）負責釋放。</summary>
public sealed record AttachmentDownload(Stream Content, string Name, long Size);

/// <summary>清掉沒有任何記錄（含軟刪除）參照、且超過寬限時間的檔案。系統管理員專用。</summary>
public sealed record SweepAttachmentsRequest(TimeSpan Grace) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record SweepAttachmentsResult(int Deleted, int Kept);

public sealed record GetAttachmentSettingsRequest : IRequest<ServiceResult>;
