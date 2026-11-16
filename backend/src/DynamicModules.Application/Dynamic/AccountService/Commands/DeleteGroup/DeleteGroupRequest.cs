using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;

public record DeleteGroupRequest(long GroupId) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
