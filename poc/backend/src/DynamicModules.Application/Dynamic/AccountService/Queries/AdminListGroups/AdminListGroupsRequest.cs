using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.AdminListGroups;

public record AdminListGroupsRequest : IRequest<ServiceResult>, ISystemAdminRequest;
