using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.UpdateReport;

public record UpdateReportRequest(long Id, string Name, ReportDefinition Definition) : IRequest<ServiceResult>, ITransactionalRequest;
