using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public record ResetPasswordRequest(string UserId, string Password) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
