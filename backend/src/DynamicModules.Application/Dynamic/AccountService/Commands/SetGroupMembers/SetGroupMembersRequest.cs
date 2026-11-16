using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.SetGroupMembers;

public record SetGroupMembersRequest(long GroupId, List<string> UserIds) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
