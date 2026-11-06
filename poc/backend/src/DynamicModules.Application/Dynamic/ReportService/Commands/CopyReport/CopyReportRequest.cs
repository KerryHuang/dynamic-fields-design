using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CopyReport;

public record CopyReportRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;
