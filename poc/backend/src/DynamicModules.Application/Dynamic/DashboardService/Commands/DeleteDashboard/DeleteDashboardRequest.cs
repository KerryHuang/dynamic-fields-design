using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.DeleteDashboard;

public record DeleteDashboardRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;
