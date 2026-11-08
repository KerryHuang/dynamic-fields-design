using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>重試安裝或恢復整合：缺的補建、相同的沿用、定義被改過的標 Blocked（不覆寫）。可重複呼叫，結果相同。</summary>
public record RetryIntegrationRequest(string AppKey, string Code)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>啟用或停用整合（只改狀態，不改定義）。</summary>
public record SetIntegrationEnabledRequest(string AppKey, string Code, bool Enabled)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public class IntegrationCommandHandlers(IAppRepository apps, IUnitOfWork uow, IntegrationReconciler reconciler, AppResponseBuilder builder)
    : IRequestHandler<RetryIntegrationRequest, ServiceResult>, IRequestHandler<SetIntegrationEnabledRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(RetryIntegrationRequest request, CancellationToken ct) =>
        RunAsync(request.AppKey, request.Code, null, retry: true, ct);

    public Task<ServiceResult> Handle(SetIntegrationEnabledRequest request, CancellationToken ct) =>
        RunAsync(request.AppKey, request.Code, request.Enabled, retry: false, ct);

    private async Task<ServiceResult> RunAsync(string appKey, string code, bool? enabled, bool retry, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(appKey, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        var row = (await apps.ListIntegrationsAsync(ct)).FirstOrDefault(r => r.AppId == app.Id && r.Code == code);
        if (row is null) return ServiceResult.NotFound("整合不存在");
        if (enabled is { } on)
        {
            row.Enabled = on;
            await uow.CommitAsync(ct);
        }
        var warnings = await reconciler.ReconcileAsync(ct, (appKey, code), retry);
        app = await apps.GetByKeyAsync(appKey, ct);
        return ServiceResult.Success(await builder.BuildAsync(app!, withDependents: false, warnings, ct));
    }
}
