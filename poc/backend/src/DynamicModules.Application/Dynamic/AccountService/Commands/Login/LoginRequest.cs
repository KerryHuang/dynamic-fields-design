using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

/// <summary>不是 ITransactionalRequest：失敗計數在回傳失敗時也要寫入，由 handler 自己提交。</summary>
public record LoginRequest(string UserId, string Password) : IRequest<ServiceResult>;
