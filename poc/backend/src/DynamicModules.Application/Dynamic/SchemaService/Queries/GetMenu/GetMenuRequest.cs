using DynamicModules.Application.Dynamic.SchemaService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;

public record GetMenuRequest : IRequest<List<MenuItem>>;
