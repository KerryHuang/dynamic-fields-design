using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminDeleteDashboard;

public record AdminDeleteDashboardRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
