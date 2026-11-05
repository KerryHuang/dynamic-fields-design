using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;

/// <summary>登入頁用，匿名可呼叫。</summary>
public record GetAuthOptionsRequest : IRequest<ServiceResult>;
