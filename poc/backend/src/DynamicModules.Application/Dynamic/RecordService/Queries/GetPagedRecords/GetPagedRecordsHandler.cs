using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;

public class GetPagedRecordsHandler(
    IModuleRepository modules, IRecordRepository records, FieldTypeRegistry registry, IBusinessTime time,
    IDynamicAccessGuard guard, ReferenceResolver resolver)
    : IRequestHandler<GetPagedRecordsRequest, ServiceResult>
{
    private const int DefaultPagesSize = 10;

    public async Task<ServiceResult> Handle(GetPagedRecordsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var size = request.PagesSize <= 0 ? DefaultPagesSize : Math.Min(request.PagesSize, PagedRequest.MaxPagesSize);
        var errors = new Dictionary<string, string[]>();
        var query = await RecordQueryBuilder.BuildAsync(modules, registry, module, perms, request.Filter, request.SortBy,
            request.SortOrder, request.Q, time.Today, page, size, errors, ct);
        if (query is null) return ServiceResult.Invalid(errors);

        var (items, total) = await records.QueryAsync(query, ct);
        var refs = await resolver.ResolveAsync(module, perms, items, ct);
        return ServiceResult.Success(new PagedResponse<DynamicRecordResponse>(
            items.Select(r => r.ToResponse(module.Key, perms, module, refs.GetValueOrDefault(r.Id))).ToList(), total, page, size));
    }
}
