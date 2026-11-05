using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;

public record GetPagedRecordsRequest(
    string ModuleKey, int CurrentPage, int PagesSize, string? Filter,
    IReadOnlyList<string> SortBy, IReadOnlyList<string> SortOrder, string? Q) : IRequest<ServiceResult>;
