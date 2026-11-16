using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Queries.GetTemplates;

public class GetTemplatesHandler(ITemplateSource templates) : IRequestHandler<GetTemplatesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTemplatesRequest request, CancellationToken ct)
    {
        var list = await templates.ListAsync(ct);
        return ServiceResult.Success(list.Select(x => Summarize(x.Package, x.Source)).ToList());
    }

    public static TemplateSummary Summarize(TemplatePackage p, string source)
    {
        var graph = TemplateGraph.Build(p);
        var codes = p.Modules.Select(m => m.Code).ToHashSet();
        return new TemplateSummary(p.Key, p.Name, p.Description, source, p.Modules.Select(m =>
        {
            var fields = m.Blocks.SelectMany(b => b.Fields).ToList();
            var detailOf = fields.Where(TemplateGraph.IsDetailReference)
                .Select(f => TemplateGraph.ConfigObject(f.Config, "refTarget") is { } t ? TemplateGraph.ConfigString(t, "key") : null)
                .FirstOrDefault(k => k is not null && codes.Contains(k));
            return new TemplateModuleSummary(m.Code, m.Key, m.Name, detailOf, fields.Select(f =>
            {
                var name = TemplateGraph.Qualify(m.Code, f.Code);
                return new TemplateFieldSummary(f.Code, f.Label, f.Type.ToString().ToLowerInvariant(),
                    graph.IsLocked(name), graph.DependsOn(name).ToList());
            }).ToList());
        }).ToList());
    }
}
