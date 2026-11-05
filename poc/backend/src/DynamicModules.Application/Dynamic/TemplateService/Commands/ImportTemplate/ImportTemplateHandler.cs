using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TemplateService.Queries.GetTemplates;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.ImportTemplate;

public class ImportTemplateHandler(
    TemplateCatalog catalog, IModuleTemplateRepository templates, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<ImportTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ImportTemplateRequest request, CancellationToken ct)
    {
        var (package, _) = TemplateJson.Parse(request.Json);
        if (package is null)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["template"] = ["範本不是有效的 JSON"] });

        var errors = TemplateValidator.Validate(package);
        if (errors.Count > 0)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["template"] = errors.ToArray() });

        if (catalog.BuiltIns.Any(p => p.Key == package.Key) || await templates.GetByKeyAsync(package.Key, ct) is not null)
            return ServiceResult.Conflict("範本代碼已存在");

        var entity = new ModuleTemplate { Key = package.Key, Json = request.Json };
        entity.StampCreated(user.UserId, time.Now);
        templates.Add(entity);
        await uow.CommitAsync(ct);

        return ServiceResult.Created(GetTemplatesHandler.Summarize(package, TemplateSource.Imported));
    }
}
