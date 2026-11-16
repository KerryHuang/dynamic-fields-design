using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;

public record ExportRecordsRequest(
    string ModuleKey, string Format, string? Filter, IReadOnlyList<string> SortBy, IReadOnlyList<string> SortOrder,
    string? Q, string Locale) : IRequest<ServiceResult>;

/// <summary>成功時的檔案內容；Controller 轉成 File()。</summary>
public record ExportFile(string FileName, string ContentType, byte[] Content);
