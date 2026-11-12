using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using System.Text.Json.Serialization;
using MediatR;

namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 上傳一個檔案到記錄的附件欄位。只能對已存檔的記錄上傳（檔案參照靠記錄的值，沒有「暫存區」）。
/// 算一次記錄更新（走寫入管線：事件、檢查、任務規則、事實鎖、分錄、簽核後盾）。
/// <see cref="Content"/> 不進冪等雜湊，改以檔名與 <see cref="Length"/> 代表檔案。
/// </summary>
public sealed record UploadAttachmentRequest(long RecordId, long FieldId, [property: JsonIgnore] Stream Content, string? FileName, long Length)
    : IRequest<ServiceResult>, IRecordWriteRequest;

/// <summary>只檢查上傳資格（權限、可寫、環境開關），不讀檔案；給控制器在收進請求主體前先擋。</summary>
public sealed record CheckAttachmentUploadRequest(long RecordId, long FieldId) : IRequest<ServiceResult>;

/// <summary>資格檢查通過時帶回的請求主體上限：單檔大小（該模組生效值）與這個請求最多幾個檔。</summary>
public sealed record UploadBodyLimit(long MaxBytes, int Files);

public sealed record UploadedAttachment(string Id, string Name, long Size, string RowVersion);

public sealed record DownloadAttachmentRequest(long RecordId, long FieldId, string FileId) : IRequest<ServiceResult>;

/// <summary>下載結果。Stream 由呼叫端（控制器）負責釋放。</summary>
public sealed record AttachmentDownload(Stream Content, string Name, long Size);

/// <summary>清掉沒有任何記錄（含軟刪除）參照、且超過寬限時間的檔案。系統管理員專用。</summary>
public sealed record SweepAttachmentsRequest(TimeSpan Grace) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record SweepAttachmentsResult(int Deleted, int Kept);

public sealed record GetAttachmentSettingsRequest : IRequest<ServiceResult>;
