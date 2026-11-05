using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;

/// <summary>不能把個人檢視改成共用或反過來；要改就另存一份。</summary>
public record UpdateViewRequest(
    long Id, I18nText Name, List<long> Columns, JsonElement? Filter, List<ViewSort> Sort, bool IsDefault)
    : IRequest<ServiceResult>, ITransactionalRequest;
