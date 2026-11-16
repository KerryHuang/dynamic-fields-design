using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

/// <summary>不是 ITransactionalRequest：舊鎖定到期的歸零在失敗時也要寫入，由 handler 自己提交。</summary>
public record LoginRequest(string UserId, string Password, string? ClientIp = null) : IRequest<ServiceResult>;
