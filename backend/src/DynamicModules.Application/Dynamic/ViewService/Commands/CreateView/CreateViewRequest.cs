using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;

public record CreateViewRequest(
    string ModuleKey, I18nText Name, bool Shared, List<long> Columns, JsonElement? Filter, List<ViewSort> Sort,
    bool IsDefault) : IRequest<ServiceResult>, ITransactionalRequest;
