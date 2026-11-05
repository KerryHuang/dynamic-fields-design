using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;

public record CreateGroupRequest(string Name, string Locale) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
