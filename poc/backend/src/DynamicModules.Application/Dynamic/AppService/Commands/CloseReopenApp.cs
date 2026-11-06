using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

public record CloseAppRequest(string Key, bool Cascade)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>
/// 關閉：Active 的成員模組改 Closed（Draft、Inactive 是客戶自己決定的狀態，保持原狀），資料、設定、權限、流水號全部不動，
/// Program.Active 也不動（讀取還要靠它通過守門員）。已經關閉再關閉回 200 且不變更。
/// </summary>
public class CloseAppHandler(
    IAppRepository apps, IModuleRepository modules, AppDependencyChecker dependencies, AppResponseBuilder builder,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time) : IRequestHandler<CloseAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CloseAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        if (app.Status == AppStatus.Closed)
            return ServiceResult.Success(new CloseAppResponse(await builder.BuildAsync(app, true, null, ct), [], []));

        var warnings = new List<string>();
        var dependents = await dependencies.DependentsOfAsync(app, ct);
        if (dependents.Apps.Count > 0 && !request.Cascade)
            return ServiceResult.ConflictWith("有其他應用程式依賴它，請先關閉它們，或選擇連帶關閉",
                new { dependents = dependents.Apps.Select(a => a.Key).ToList() });
        warnings.AddRange(dependents.UnaffiliatedReferrers.Select(k => $"自建模組「{k}」有欄位參照這個應用程式，關閉後它無法新增指向這裡的資料"));

        var toClose = request.Cascade ? await dependencies.CascadeOrderAsync(app, ct) : [app];
        foreach (var target in toClose)
        {
            foreach (var member in target.Modules)
            {
                var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
                if (module is not { Status: ModuleStatus.Active }) continue;
                module.Status = ModuleStatus.Closed;
                module.TouchVersion(user.UserId, time.Now);
            }
            target.Status = AppStatus.Closed;
            target.TouchRowVersion();
            target.StampModified(user.UserId, time.Now);
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new CloseAppResponse(await builder.BuildAsync(app, true, null, ct),
            toClose.Select(a => a.Key).ToList(), warnings));
    }
}

public record ReopenAppRequest(string Key)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>
/// 重新開啟：它依賴的應用程式與模組都必須是 Open／Active，成員模組 Closed 改回 Active。關閉期間資料與設定沒有被動過，
/// 所以只是狀態翻回去。連帶關閉的依賴者不會自動重新開啟。
/// </summary>
public class ReopenAppHandler(
    IAppRepository apps, IModuleRepository modules, IProgramRepository programs, AppDependencyChecker dependencies,
    AppResponseBuilder builder, IUnitOfWork uow, ICurrentUser user, IBusinessTime time) : IRequestHandler<ReopenAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ReopenAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        if (app.Status == AppStatus.Open) return ServiceResult.Success(await builder.BuildAsync(app, true, null, ct));

        var requirement = await dependencies.CheckRequirementsAsync(app.Requires, ct);
        if (requirement.Missing.Count > 0)
            return ServiceResult.ConflictWith("它依賴的應用程式或模組還沒有開啟", new { missingDependencies = requirement.Missing });

        foreach (var member in app.Modules)
        {
            var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
            if (module is not { Status: ModuleStatus.Closed }) continue;
            module.Status = ModuleStatus.Active;
            if (await programs.GetAsync(module.ProgramCode, ct) is { } program) program.Active = true;
            module.TouchVersion(user.UserId, time.Now);
        }
        app.Status = AppStatus.Open;
        app.TouchRowVersion();
        app.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await builder.BuildAsync(app, true, null, ct));
    }
}
