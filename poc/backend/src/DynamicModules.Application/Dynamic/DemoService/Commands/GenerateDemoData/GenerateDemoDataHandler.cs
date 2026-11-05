using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;

/// <summary>衝突檢查（交易已由 TransactionBehavior 開始）→ 建示範人員 → 建結構 → 規劃各情境的步驟 → 依模擬時間排序執行。</summary>
public class GenerateDemoDataHandler(DemoPreflight preflight, IDemoPeople people, DemoStructure structure)
    : IRequestHandler<GenerateDemoDataRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GenerateDemoDataRequest request, CancellationToken ct)
    {
        var conflicts = await preflight.ConflictsAsync(ct);
        if (conflicts.Count > 0)
            return ServiceResult.ConflictWith("已有相同的資料，未產生 Demo 資料", new DemoConflicts([.. conflicts]));

        await people.AddAsync(ct);

        var (context, failed) = await structure.BuildAsync(request.Today, ct);
        if (failed is not null) return failed;

        var steps = DemoTimeline.Plan(request.Today, DemoCatalog.Scenarios);
        failed = await DemoTimeline.RunAsync(context!, steps, request.Progress, ct);
        if (failed is not null) return failed;
        return ServiceResult.Success(new DemoGenerateResult(steps.Count));
    }
}
