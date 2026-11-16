using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DemoService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Queries.GetDemoDataStatus;

/// <summary>
/// 沒有另存標記：執行器在跑是 running；這個程序內最後一次成功、失敗分別是 generated、failed；
/// 否則看衝突檢查——沒有衝突 notGenerated、四個範本的模組代碼全部存在 generated、其他 conflict。
/// </summary>
public class GetDemoDataStatusHandler(IDemoDataRunner runner, DemoPreflight preflight)
    : IRequestHandler<GetDemoDataStatusRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetDemoDataStatusRequest request, CancellationToken ct)
    {
        var s = runner.Current;
        switch (s.State)
        {
            case DemoRunState.Running: return ServiceResult.Success(DemoDataStatus.Of(s, "running"));
            case DemoRunState.Succeeded: return ServiceResult.Success(DemoDataStatus.Of(s, "generated"));
            case DemoRunState.Failed: return ServiceResult.Success(DemoDataStatus.Of(s, "failed"));
        }

        var conflicts = await preflight.ConflictsAsync(ct);
        var state = conflicts.Count == 0 ? "notGenerated"
            : await preflight.AllPresentAsync(ct) ? "generated"
            : "conflict";
        return ServiceResult.Success(new DemoDataStatus(
            state, state == "conflict" ? [.. conflicts] : [], null, null, null, 0, 0));
    }
}
