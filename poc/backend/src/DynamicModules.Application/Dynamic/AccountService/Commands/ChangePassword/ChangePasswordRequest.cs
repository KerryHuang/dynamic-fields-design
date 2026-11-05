using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public record ChangePasswordRequest(string OldPassword, string NewPassword) : IRequest<ServiceResult>, ITransactionalRequest;
