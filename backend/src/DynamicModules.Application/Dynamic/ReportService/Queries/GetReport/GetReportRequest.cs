using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReport;

public record GetReportRequest(long Id) : IRequest<ServiceResult>;
