using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;

public class GetMenuHandler(IModuleRepository modules, IDynamicAccessGuard guard)
    : IRequestHandler<GetMenuRequest, List<MenuItem>>
{
    public async Task<List<MenuItem>> Handle(GetMenuRequest request, CancellationToken ct)
    {
        var readable = new List<MenuItem>();
        // 只列有 Read 的模組（§11.2）；Program 未啟用或沒授權都由 guard 擋下
        foreach (var m in (await modules.ListReadableAsync(ct)).Where(m => !ModuleRelations.IsDetail(m))) // 明細模組不進選單
            if ((await guard.AuthorizeAsync(m, PermissionAction.Read, ct)).Permissions is not null)
                readable.Add(new MenuItem(m.Key, m.NameI18n, m.Icon, m.Section, m.Status == ModuleStatus.Closed));
        return readable
            .OrderBy(m => m.Name.Resolve(I18nText.FallbackLocale), StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .ToList();
    }
}
