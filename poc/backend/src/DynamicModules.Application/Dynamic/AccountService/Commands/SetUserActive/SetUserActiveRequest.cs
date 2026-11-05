using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.SetUserActive;

public record SetUserActiveRequest(string UserId, bool IsActive) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
