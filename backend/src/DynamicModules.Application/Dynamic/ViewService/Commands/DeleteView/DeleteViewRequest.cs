using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.DeleteView;

public record DeleteViewRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;
