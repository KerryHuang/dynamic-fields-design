using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReports;

public record GetReportsRequest : IRequest<ServiceResult>;
