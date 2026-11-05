using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;

public record GetModuleSchemaRequest(string ModuleKey) : IRequest<ServiceResult>;
