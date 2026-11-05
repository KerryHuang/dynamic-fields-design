using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.DemoService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.StartDemoData;

/// <summary>衝突檢查只是提早回報；防重入靠執行器的鎖（兩個請求可能同時通過檢查）。</summary>
public class StartDemoDataHandler(IDynamicAccessGuard guard, DemoPreflight preflight, IDemoDataRunner runner)
    : IRequestHandler<StartDemoDataRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(StartDemoDataRequest request, CancellationToken ct)
    {
        if (!(await guard.DesignerActionsAsync(ct)).Contains(PermissionAction.Edit))
            return ServiceResult.Forbidden();

        if (runner.Current.State == DemoRunState.Running) return ServiceResult.Conflict("Demo 資料產生中，請稍候");

        var conflicts = await preflight.ConflictsAsync(ct);
        if (conflicts.Count > 0)
            return ServiceResult.ConflictWith("已有同代碼的模組，未產生 Demo 資料", new DemoConflicts([.. conflicts]));

        if (!runner.TryStart()) return ServiceResult.Conflict("Demo 資料產生中，請稍候");
        return ServiceResult.Success(DemoDataStatus.Of(runner.Current, "running"));
    }
}
