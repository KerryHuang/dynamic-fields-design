using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;

public record CreateReportRequest(string Name, ReportDefinition Definition) : IRequest<ServiceResult>, ITransactionalRequest;
