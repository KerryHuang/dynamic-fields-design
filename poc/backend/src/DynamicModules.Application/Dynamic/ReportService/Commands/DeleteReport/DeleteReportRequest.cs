using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.DeleteReport;

public record DeleteReportRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;
