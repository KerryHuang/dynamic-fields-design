using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.RelationService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedPaged;

public class GetRelatedPagedHandler(
    IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, ReferenceResolver resolver)
    : IRequestHandler<GetRelatedPagedRequest, ServiceResult>
{
    private const int DefaultSize = 20;

    public async Task<ServiceResult> Handle(GetRelatedPagedRequest request, CancellationToken ct)
    {
        var (target, error) = await RelatedTabs.ReadTargetAsync(request.RecordId, modules, records, guard, ct);
        if (target is null) return error!;
        var match = (await RelatedTabs.VisibleAsync(target.Key, modules, guard, ct)).FirstOrDefault(x => x.Field.Id == request.FieldId);
        if (match.Source is null) return ServiceResult.NotFound(RelatedTabs.RelationNotFound);

        // 來源模組要連同選項與規則載入才能組回應
        var source = (await modules.GetByIdAsync(match.Source.Id, includeChildren: true, ct))!;
        var page = request.Page <= 0 ? 1 : request.Page;
        var size = request.Size <= 0 ? DefaultSize : Math.Min(request.Size, PagedRequest.MaxPagesSize);
        var (items, total) = await records.QueryByRefAsync(
            request.FieldId, request.RecordId.ToString(), match.Permissions.OwnerUserIds, page, size, ct);
        var refs = await resolver.ResolveAsync(source, match.Permissions, items, ct);
        return ServiceResult.Success(new PagedResponse<DynamicRecordResponse>(
            items.Select(r => r.ToResponse(source.Key, match.Permissions, source, refs.GetValueOrDefault(r.Id))).ToList(), total, page, size));
    }
}
