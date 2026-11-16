using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;

/// <summary>Modules 只需列要改的模組；ExcludeFields 是 &lt;模組代號&gt;.&lt;欄位代號&gt;。兩個清單為 null 時視為空清單。</summary>
public record InstantiateTemplateRequest(string TemplateKey, List<InstantiateModuleInput> Modules, List<string> ExcludeFields)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public List<InstantiateModuleInput> Modules { get; init; } = Modules ?? [];
    public List<string> ExcludeFields { get; init; } = ExcludeFields ?? [];

    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}

/// <summary>Key、Name 為 null 時用範本的。Code 宣告成可為 null，讓空白由 InstantiateTemplateValidator 回繁中訊息（不走模型繫結的英文訊息）。</summary>
public record InstantiateModuleInput(string? Code, string? Key, I18nText? Name);
