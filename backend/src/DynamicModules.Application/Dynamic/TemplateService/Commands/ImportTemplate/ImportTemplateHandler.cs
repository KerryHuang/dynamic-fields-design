using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TemplateService.Queries.GetTemplates;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.ImportTemplate;

public class ImportTemplateHandler(
    TemplateCatalog catalog, IModuleTemplateRepository templates, IUnitOfWork uow, ICurrentUser user, IBusinessTime time, AuditContext audit)
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
        // 驗證過才補稽核目標：缺名稱的本文要回 400，不能在這裡遇到 null
        audit.Target(package.Key, package.Name.Resolve(I18nText.FallbackLocale));

        var entity = new ModuleTemplate { Key = package.Key, Json = request.Json };
        entity.StampCreated(user.UserId, time.Now);
        templates.Add(entity);
        await uow.CommitAsync(ct);

        return ServiceResult.Created(GetTemplatesHandler.Summarize(package, TemplateSource.Imported));
    }
}
