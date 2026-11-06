using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

public record CatalogModule(string Key, string Name);

/// <summary>有 Read 權限、啟用或已關閉（能查閱）且非明細的模組。</summary>
public class GetReportMainModulesHandler(IModuleRepository modules, IDynamicAccessGuard guard)
    : IRequestHandler<GetReportMainModulesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetReportMainModulesRequest request, CancellationToken ct)
    {
        var result = new List<CatalogModule>();
        foreach (var m in await modules.ListReadableAsync(ct))
        {
            if (ModuleRelations.IsDetail(m)) continue;
            if ((await guard.AuthorizeAsync(m, PermissionAction.Read, ct)).Permissions is null) continue;
            result.Add(new CatalogModule(m.Key, m.NameI18n.Resolve(request.Locale)));
        }
        return ServiceResult.Success(result);
    }
}
