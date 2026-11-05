using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RelationService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedTabs;

public class GetRelatedTabsHandler(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard)
    : IRequestHandler<GetRelatedTabsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRelatedTabsRequest request, CancellationToken ct)
    {
        var (target, error) = await RelatedTabs.ReadTargetAsync(request.RecordId, modules, records, guard, ct);
        if (target is null) return error!;
        var tabs = new List<RelatedTab>();
        foreach (var (source, field, perms) in await RelatedTabs.VisibleAsync(target.Key, modules, guard, ct))
            tabs.Add(new RelatedTab(field.Id, field.Key, field.LabelI18n, source.Key, source.NameI18n,
                perms.Can(PermissionAction.Add) && perms.CanWrite(field) && !field.Config.ReadOnly));
        return ServiceResult.Success(tabs);
    }
}
