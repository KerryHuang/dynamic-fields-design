using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Queries.ExportTemplate;

public class ExportTemplateHandler(IModuleRepository modules, TemplateExporter exporter)
    : IRequestHandler<ExportTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ExportTemplateRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        var package = await exporter.ExportAsync(module, ct);
        return ServiceResult.Success(new ExportFile(
            $"{module.Key}.template.json", "application/json", JsonSerializer.SerializeToUtf8Bytes(package, TemplateJson.Options)));
    }
}
