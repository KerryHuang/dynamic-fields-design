using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.RenameGroup;

public record RenameGroupRequest(long GroupId, string Name, string Locale) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
