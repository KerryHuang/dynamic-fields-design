using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportShareTargets;

public record GetReportShareTargetsRequest : IRequest<ServiceResult>;
