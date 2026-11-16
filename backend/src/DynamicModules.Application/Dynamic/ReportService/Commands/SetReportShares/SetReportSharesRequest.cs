using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;

public record SetReportSharesRequest(long Id, List<long> GroupIds) : IRequest<ServiceResult>, ITransactionalRequest;
