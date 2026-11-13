using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Queries;

public record GetAppPackagesRequest : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppPackagesHandler(AppPackageSource packages, IAppRepository apps) : IRequestHandler<GetAppPackagesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppPackagesRequest request, CancellationToken ct)
    {
        var status = (await apps.ListAsync(ct)).ToDictionary(a => a.Key, a => AppMapper.StatusName(a.Status));
        // 共用主檔不列在可開啟清單：它由使用它的應用程式自動開啟（共用主檔 §4.1）
        var list = (await packages.ListAsync(ct)).Where(p => !p.Package.Manifest.IsMaster)
            .Select(p => AppResponseBuilder.Summarize(p, status.GetValueOrDefault(p.Package.Manifest.Id))).ToList();
        return ServiceResult.Success(list);
    }
}

public record GetAppPackageRequest(string Id) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppPackageHandler(AppPackageSource packages, IAppRepository apps) : IRequestHandler<GetAppPackageRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppPackageRequest request, CancellationToken ct)
    {
        var source = await packages.FindAsync(request.Id, ct);
        if (source is null) return ServiceResult.NotFound(AppMessages.PackageNotFound);
        var app = await apps.GetByKeyAsync(request.Id, ct);
        return ServiceResult.Success(AppResponseBuilder.Summarize(source, app is null ? null : AppMapper.StatusName(app.Status)));
    }
}

public record GetAppsRequest : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppsHandler(IAppRepository apps, AppResponseBuilder builder) : IRequestHandler<GetAppsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppsRequest request, CancellationToken ct)
    {
        var result = new List<AppResponse>();
        foreach (var app in await apps.ListAsync(ct)) result.Add(await builder.BuildAsync(app, withDependents: false, null, ct));
        return ServiceResult.Success(result);
    }
}

public record GetAppRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppHandler(IAppRepository apps, AppResponseBuilder builder) : IRequestHandler<GetAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        return app is null ? ServiceResult.NotFound(AppMessages.NotFound)
            : ServiceResult.Success(await builder.BuildAsync(app, withDependents: true, null, ct));
    }
}

/// <summary>預覽：做開啟的所有前置檢查但不寫入。要系統管理員加設計器 Read。</summary>
public record PreviewOpenAppRequest(string PackageId, OpenAppBody Body) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class PreviewOpenAppHandler(AppOpenPlanner planner) : IRequestHandler<PreviewOpenAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewOpenAppRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(request.PackageId, request.Body, ct);
        return plan.Failure ?? ServiceResult.Success(plan.Preview);
    }
}

/// <summary>匯出結果：檔案本身保持乾淨，warnings（表達不了而略過的項目）由控制器放在回應標頭。</summary>
public record AppExportResult(ExportFile File, IReadOnlyList<string> Warnings);

/// <summary>由目前的成員模組重新產生套件檔；成功時 Data 是 AppExportResult。</summary>
public record ExportAppRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class ExportAppHandler(IAppRepository apps, AppExporter exporter) : IRequestHandler<ExportAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ExportAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        var exported = await exporter.ExportAsync(app, ct);
        if (exported.Errors.Count > 0) return ServiceResult.ConflictWith(exported.Errors[0], new { errors = exported.Errors });
        return ServiceResult.Success(new AppExportResult(new ExportFile(
            $"{app.Key}.app.json", "application/json", JsonSerializer.SerializeToUtf8Bytes(exported.Package, TemplateJson.Options)),
            exported.Warnings));
    }
}
