using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetLightRecords;

/// <summary>模組記錄的輕量查詢（給參照選擇器）：回傳 Id 與 Title，受 Read 權限與資料範圍限制。</summary>
public class GetLightRecordsHandler(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard)
    : IRequestHandler<GetLightRecordsRequest, ServiceResult>
{
    public const int MaxSize = 50;
    public const int DefaultSize = 20;

    public async Task<ServiceResult> Handle(GetLightRecordsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsWritable() || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return access.Denied!;
        if (!module.TitleVisibleTo(perms)) return ServiceResult.Forbidden();

        var page = Math.Max(request.Page, 1);
        var size = request.Size <= 0 ? DefaultSize : Math.Min(request.Size, MaxSize);
        var keyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : request.Keyword.Trim();
        var (items, total) = await records.QueryLightAsync(module.Id, keyword, perms.OwnerUserIds, page, size, ct);
        return ServiceResult.Success(new LightPage(items.Select(i => new LightItem(i.Id.ToString(), i.Title)).ToList(), total));
    }
}
