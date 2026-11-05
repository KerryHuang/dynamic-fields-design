using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;

public class InstantiateTemplateHandler(ITemplateSource templates, TemplateInstantiator instantiator)
    : IRequestHandler<InstantiateTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(InstantiateTemplateRequest request, CancellationToken ct)
    {
        var package = await templates.FindAsync(request.TemplateKey, ct);
        if (package is null) return ServiceResult.NotFound("範本不存在");

        var overrides = new Dictionary<string, InstantiateModuleInput>();
        foreach (var m in request.Modules) overrides[m.Code!] = m; // InstantiateTemplateValidator 已擋空白與重複
        return await instantiator.InstantiateAsync(package, overrides, request.ExcludeFields.ToHashSet(), ct);
    }
}
