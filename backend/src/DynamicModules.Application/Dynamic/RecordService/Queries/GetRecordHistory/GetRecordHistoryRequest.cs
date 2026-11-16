using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;

public record GetRecordHistoryRequest(long Id, int CurrentPage, int PagesSize, string Locale) : IRequest<ServiceResult>;
