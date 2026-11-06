using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.RestoreDashboard;

public record RestoreDashboardRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;
