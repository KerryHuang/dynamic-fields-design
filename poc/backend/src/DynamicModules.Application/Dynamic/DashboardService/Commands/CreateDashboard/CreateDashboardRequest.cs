using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.CreateDashboard;

public record CreateDashboardRequest(string Name, DashboardLayout Layout) : IRequest<ServiceResult>, ITransactionalRequest;
