using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Queries.GetViews;

public record GetViewsRequest(string ModuleKey) : IRequest<ServiceResult>;
