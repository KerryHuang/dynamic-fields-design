using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;

public record AdminSetDashboardAssignmentsRequest(long Id, List<long> GroupIds) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
