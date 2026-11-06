using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;

public record AdminCreateDashboardRequest(string Name, DashboardLayout Layout) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
